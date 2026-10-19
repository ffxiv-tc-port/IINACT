using Advanced_Combat_Tracker;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using IINACT.Network;
using Machina.FFXIV.Dalamud;

namespace IINACT;

// 本檔契約：Tick 只在框架執行緒跑、每秒取樣一次，取樣與判斷不配置記憶體，只有狀態轉換才組字串寫 log。
// 偵測一律開，每次事件開始與解除各寫一行 Information。
// 自動恢復（ParserWatchdogAutoRecover，預設關）只重開「已經結束」的內部執行緒；還活著但卡住的刻意不碰——
// 另開一條會和它搶同一批狀態，唯一解是重新載入插件，而從插件自己裡面熱重載會碰到舊 ALC 回收與
// TLS 索引重用的問題，所以只提示使用者手動做。

public readonly record struct ParserHealth(
    bool ScanThreadExited,
    string? ScanExitReason,
    long ScanBusySince,
    string ScanPhase,
    int PendingRefreshes,
    long LastNetworkLineTicks,
    int ParserLogQueueLength);

public enum StallKind
{
    None,
    ScanThreadExited,
    ScanThreadStuck,
    LogReaderExited,
    LogReaderStuck,
    CombatActionThreadExited,
    CombatActionStuck,
    PacketQueueStuck,
    NoCombatData,
}

/// <summary>每秒一次的取樣。時間一律是 <see cref="Environment.TickCount64"/>，0＝沒有發生過。</summary>
public readonly record struct WatchdogSample(
    long Now,
    long CombatSince,
    ParserHealth Health,
    WorkerThreadState LogReader,
    WorkerThreadState CombatActionThread,
    int PendingCombatActions,
    int PacketQueueLength,
    bool NetworkHooked,
    long LastPacketTicks);

public sealed class ParserWatchdog
{
    public const long StartupGraceMs = 90_000;
    public const long ScanStuckMs = 30_000;

    // 深層迷宮的變身陷阱會讓人在戰鬥中整整 30 秒沒有任何網路行（實測），門檻要明顯高過它。
    public const long CombatSilenceMs = 60_000;

    // 這三個佇列正常每 2～50 毫秒就清空一次。
    public const int QueueStuckLength = 5000;
    public const int MaxRestartsPerKind = 3;

    private readonly FfxivActPluginWrapper wrapper;
    private readonly Func<ZoneDownHookManager?> hookManager;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IChatGui chatGui;
    private readonly Configuration configuration;

    private readonly long startedAt = Environment.TickCount64;
    private readonly int[] restarts = new int[Enum.GetValues<StallKind>().Length];
    private long lastTick;
    private long combatSince;
    private long currentSince;
    private bool recoveryHandled;

    public ParserWatchdog(
        FfxivActPluginWrapper wrapper, Func<ZoneDownHookManager?> hookManager, ICondition condition,
        IClientState clientState, IChatGui chatGui, Configuration configuration)
    {
        this.wrapper = wrapper;
        this.hookManager = hookManager;
        this.condition = condition;
        this.clientState = clientState;
        this.chatGui = chatGui;
        this.configuration = configuration;
    }

    public StallKind Current { get; private set; }

    public bool WarmingUp => Environment.TickCount64 - startedAt < StartupGraceMs;

    public static StallKind Evaluate(in WatchdogSample s)
    {
        var h = s.Health;
        if (h.ScanThreadExited)
            return StallKind.ScanThreadExited;
        if (h.ScanBusySince != 0 && s.Now - h.ScanBusySince >= ScanStuckMs)
            return StallKind.ScanThreadStuck;
        if (s.LogReader == WorkerThreadState.Exited)
            return StallKind.LogReaderExited;
        if (s.LogReader == WorkerThreadState.Running && h.ParserLogQueueLength >= QueueStuckLength)
            return StallKind.LogReaderStuck;
        if (s.CombatActionThread == WorkerThreadState.Exited)
            return StallKind.CombatActionThreadExited;
        if (s.CombatActionThread == WorkerThreadState.Running && s.PendingCombatActions >= QueueStuckLength)
            return StallKind.CombatActionStuck;
        if (s.PacketQueueLength >= QueueStuckLength)
            return StallKind.PacketQueueStuck;
        if (s.NetworkHooked && s.CombatSince != 0 && s.Now - s.CombatSince >= CombatSilenceMs &&
            (h.LastNetworkLineTicks == 0 || s.Now - h.LastNetworkLineTicks >= CombatSilenceMs))
            return StallKind.NoCombatData;
        return StallKind.None;
    }

    public void Tick(IFramework _)
    {
        var now = Environment.TickCount64;
        if (now - lastTick < 1000)
            return;
        lastTick = now;

        if (!condition[ConditionFlag.InCombat])
            combatSince = 0;
        else if (combatSince == 0)
            combatSince = now;

        if (now - startedAt < StartupGraceMs)
            return;

        var form = ActGlobals.oFormActMain;
        var hook = hookManager();
        var sample = new WatchdogSample(
            now, combatSince, wrapper.GetHealth(),
            form.LogReaderThreadState, form.CombatActionThreadState, form.PendingCombatActions,
            DalamudClient.MessageQueue?.Count ?? -1,
            hook?.IsHooked ?? false, hook?.LastPacketTicks ?? 0);

        var kind = Evaluate(sample);
        if (kind != Current)
            Transition(kind, sample);
        if (kind != StallKind.None && !recoveryHandled && configuration.ParserWatchdogAutoRecover)
            Recover(kind);
    }

    private void Transition(StallKind kind, in WatchdogSample s)
    {
        if (Current != StallKind.None)
            Plugin.Log.Information(
                $"[解析器監看] 已解除：{LogName(Current)}（持續 {(s.Now - currentSince) / 1000} 秒）");
        if (kind != StallKind.None)
            Plugin.Log.Information($"[解析器監看] 偵測到：{LogName(kind)}；{Describe(s)}");

        Current = kind;
        currentSince = s.Now;
        recoveryHandled = false;
    }

    private void Recover(StallKind kind)
    {
        recoveryHandled = true;
        var name = UiName(kind).Loc();

        if (kind is not (StallKind.ScanThreadExited or StallKind.LogReaderExited or StallKind.CombatActionThreadExited))
        {
            Plugin.Log.Information($"[解析器監看] {LogName(kind)}：執行緒還活著，沒有安全的自動恢復方式，只能手動停用再啟用 IINACT");
            chatGui.Print("IINACT: ?? - this cannot be fixed automatically. Disable and re-enable IINACT in the plugin installer.".Loc(name));
            return;
        }

        if (restarts[(int)kind] >= MaxRestartsPerKind)
        {
            Plugin.Log.Information($"[解析器監看] {LogName(kind)}：本次載入已自動重啟 {MaxRestartsPerKind} 次，不再重啟");
            chatGui.Print("IINACT: ?? again - the automatic restart limit has been reached.".Loc(name));
            return;
        }

        var form = ActGlobals.oFormActMain;
        var restarted = kind switch
        {
            StallKind.ScanThreadExited => wrapper.TryRestartScanThread(),
            StallKind.LogReaderExited => form.TryRestartLogReaderThread(),
            _ => form.TryRestartCombatActionThread(),
        };
        if (!restarted)
            return;

        restarts[(int)kind]++;
        Plugin.Log.Information($"[解析器監看] 已重新啟動：{LogName(kind)}（本次載入第 {restarts[(int)kind]} 次）");
        chatGui.Print("IINACT: ?? - restarted it.".Loc(name));
    }

    private string Describe(in WatchdogSample s)
    {
        var h = s.Health;
        var scan = h.ScanThreadExited ? $"已結束（{h.ScanExitReason ?? "原因不明"}）"
                   : h.ScanBusySince != 0 ? $"在「{h.ScanPhase}」已 {(s.Now - h.ScanBusySince) / 1000} 秒"
                   : $"「{h.ScanPhase}」";
        return $"掃描執行緒 {scan}、待處理刷新 {h.PendingRefreshes}、" +
               $"距上一筆網路行 {Seconds(s.Now, h.LastNetworkLineTicks)}、" +
               $"距上一個封包 {(s.NetworkHooked ? Seconds(s.Now, s.LastPacketTicks) : "沒有掛 hook")}、" +
               $"封包佇列 {s.PacketQueueLength}、解析輸出佇列 {h.ParserLogQueueLength}、" +
               $"讀行執行緒 {s.LogReader}、戰鬥動作執行緒 {s.CombatActionThread}（佇列 {s.PendingCombatActions}）、" +
               $"戰鬥中 {(s.CombatSince == 0 ? "否" : $"{(s.Now - s.CombatSince) / 1000} 秒")}、" +
               $"區域 {clientState.TerritoryType}";
    }

    private static string Seconds(long now, long then) => then == 0 ? "從未" : $"{(now - then) / 1000} 秒";

    private static string LogName(StallKind kind) => kind switch
    {
        StallKind.ScanThreadExited => "記憶體掃描執行緒意外結束",
        StallKind.ScanThreadStuck => "記憶體掃描執行緒卡在同一輪",
        StallKind.LogReaderExited => "讀取解析輸出的執行緒意外結束",
        StallKind.LogReaderStuck => "解析輸出佇列堆積（讀行執行緒卡住）",
        StallKind.CombatActionThreadExited => "戰鬥動作執行緒意外結束",
        StallKind.CombatActionStuck => "戰鬥動作佇列堆積（執行緒卡住）",
        StallKind.PacketQueueStuck => "網路封包佇列堆積（解析器沒有在收封包）",
        StallKind.NoCombatData => "戰鬥中一段時間沒有任何網路行",
        _ => "正常",
    };

    public static string UiName(StallKind kind) => kind switch
    {
        StallKind.ScanThreadExited => "Memory scan thread stopped",
        StallKind.ScanThreadStuck => "Memory scan thread is stuck",
        StallKind.LogReaderExited => "Log line reader stopped",
        StallKind.LogReaderStuck => "Log line reader is stuck",
        StallKind.CombatActionThreadExited => "Combat data thread stopped",
        StallKind.CombatActionStuck => "Combat data thread is stuck",
        StallKind.PacketQueueStuck => "Network packets are not being processed",
        StallKind.NoCombatData => "In combat but no combat data",
        _ => "Normal",
    };
}
