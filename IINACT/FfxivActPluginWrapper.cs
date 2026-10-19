using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Advanced_Combat_Tracker;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIV_ACT_Plugin;
using FFXIV_ACT_Plugin.Common;
using FFXIV_ACT_Plugin.Config;
using FFXIV_ACT_Plugin.Logfile;
using FFXIV_ACT_Plugin.Memory;
using FFXIV_ACT_Plugin.Memory.MemoryProcessors;
using FFXIV_ACT_Plugin.Memory.MemoryReader;
using FFXIV_ACT_Plugin.Memory.Models.Global;
using FFXIV_ACT_Plugin.Parse;
using FFXIV_ACT_Plugin.Resource;
using IINACT.Network;
using Microsoft.MinIoC;
using ACTWrapper = FFXIV_ACT_Plugin.Common.ACTWrapper;

namespace IINACT;

public partial class FfxivActPluginWrapper : IDisposable
{
    private readonly Configuration configuration;
    private readonly ClientLanguage dalamudClientLanguage;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly ICondition condition;

    private readonly FFXIV_ACT_Plugin.FFXIV_ACT_Plugin ffxivActPlugin;
    private readonly Container iocContainer;
    private ISettingsMediator settingsMediator = null!;
    private readonly ParseMediator parseMediator;

    private readonly ServerTimeProcessor serverTimeProcessor;
    private readonly MobArrayProcessor mobArrayProcessor;
    private readonly IZoneMapProcessor zoneMapProcessor;
    private readonly CombatantManager combatantManager;
    private readonly IPlayerProcessor playerProcessor;
    private readonly IPartyProcessor partyProcessor;

    private readonly int mobArraySize;
    private readonly int combatantSize;
    private readonly int combatantBufferSize;
    private readonly nint mobData;
    private readonly nint[] mobDataOffsets;
    private readonly SemaphoreSlim refreshSemaphore = new(0);
    private byte mobDataAge = byte.MaxValue;

    private Thread scanThread;
    private readonly CancellationTokenSource cancellationTokenSource;
    private bool disposed;

    // 掃描執行緒寫、解析器監看每秒讀；scanBusySince 0＝正在等下一幀。
    private volatile string scanPhase = "not started";
    private volatile string? scanExitReason;
    private long scanBusySince;
    private long lastNetworkLineTicks;
    private readonly Queue<string>? parserLogQueue;

    private readonly ILogOutput logOutput;
    private readonly ILogFormat logFormat;
    private readonly IProcessManager processManager;

    public DataCollectionSettingsEventArgs DataCollectionSettings = null!;
    public ParseSettings ParseSettings = null!;
    public readonly IDataRepository Repository;
    public readonly IDataSubscription Subscription;

    public unsafe FfxivActPluginWrapper(
        Configuration configuration, ClientLanguage dalamudClientLanguage, IChatGui chatGui, IFramework framework,
        ICondition condition)
    {
        this.configuration = configuration;
        this.dalamudClientLanguage = dalamudClientLanguage;
        this.chatGui = chatGui;
        this.framework = framework;
        this.condition = condition;

        ffxivActPlugin = new FFXIV_ACT_Plugin.FFXIV_ACT_Plugin();
        Plugin.Log.Information($"Initializing FFXIV_ACT_Plugin version {typeof(FFXIV_ACT_Plugin.FFXIV_ACT_Plugin).Assembly.GetName().Version}");
        ffxivActPlugin.ConfigureIOC();

        iocContainer = ffxivActPlugin._iocContainer;
        iocContainer.Resolve<ResourceManager>().LoadResources();

        Subscription = iocContainer.Resolve<DataSubscription>();
        ffxivActPlugin.SetProperty("DataSubscription", Subscription);

        parseMediator = iocContainer.Resolve<ParseMediator>();

        ffxivActPlugin._dataCollection = iocContainer.Resolve<DataCollection>();

        logOutput = ffxivActPlugin._dataCollection._logOutput;
        logFormat = ffxivActPlugin._dataCollection._logFormat;
        parserLogQueue = (logOutput as LogOutput)?._LogQueue;

        var scanMemory = (ScanMemory)ffxivActPlugin._dataCollection._scanMemory;

        processManager = scanMemory._processManager;
        serverTimeProcessor = (ServerTimeProcessor)scanMemory._serverTimeProcessor;
        mobArrayProcessor = (MobArrayProcessor)scanMemory._mobArrayProcessor;
        zoneMapProcessor = scanMemory._zoneProcessor;
        combatantManager = (CombatantManager)scanMemory._combatantManager;
        playerProcessor = scanMemory._playerProcessor;
        partyProcessor = scanMemory._partyProcessor;

        SetupActWrapper();

        SetupDataSubscription();

        SetupSettingsMediator();

        Repository = iocContainer.Resolve<IDataRepository>();
        ffxivActPlugin.SetProperty("DataRepository", Repository);

        ffxivActPlugin._dataCollection.StartMemory();

        SetChatMessageLoggingEnabled(configuration.LogChatMessages);
        ActGlobals.oFormActMain.BeforeLogLineRead += OFormActMain_BeforeLogLineRead;
        serverTimeProcessor.ServerTime = DateTime.Now;

        cancellationTokenSource = new CancellationTokenSource();
        scanThread = StartScanThread();

        mobArraySize = mobArrayProcessor._internalMmobArray.Length;
        var combatantProcessor = (CombatantProcessor)combatantManager._combatantProcessor;
        combatantBufferSize = ((ReadCombatant)combatantProcessor._readCombatant)._buffer.Length;
        combatantSize = sizeof(CombatantStruct);
        mobData = Marshal.AllocHGlobal((mobArraySize * combatantSize) + (combatantBufferSize - combatantSize));
        mobDataOffsets = new nint[mobArraySize];
        for (var i = 0; i < mobArraySize; i++)
            mobDataOffsets[i] = mobData + (i * combatantSize);

        this.framework.Update += MobDataRefresh;
    }

    private Language ClientLanguage =>
        dalamudClientLanguage switch
        {
            Dalamud.Game.ClientLanguage.Japanese => Language.Japanese,
            Dalamud.Game.ClientLanguage.English => Language.English,
            Dalamud.Game.ClientLanguage.German => Language.German,
            Dalamud.Game.ClientLanguage.French => Language.French,
            _ when dalamudClientLanguage.ToString().Contains("Simplified") => Language.Chinese,
            _ when dalamudClientLanguage.ToString().Contains("Traditional") => Language.TraditionalChinese,
            _ => Language.TraditionalChinese
        };

    private const int TeardownTimeoutMs = 3000;

    /// <summary>Dispose 之後才有意義。false＝FFXIV_ACT_Plugin 的執行緒可能還在跑，它碰得到的東西都不可以拆。</summary>
    public bool StoppedCleanly { get; private set; }

    /// <summary>
    /// 框架執行緒（Dalamud 卸載）呼叫。FFXIV_ACT_Plugin 的關閉放到執行緒池、連同掃描執行緒最多等 3 秒：
    /// 逾時就讓它在背景自己跑完，遊戲畫面不跟著卡住。
    /// 沒有乾淨停下時 mobData 刻意不釋放（掃描或解析執行緒可能還在讀它），漏一塊記憶體換不崩。
    /// </summary>
    public void Dispose()
    {
        disposed = true;
        cancellationTokenSource.Cancel();
        framework.Update -= MobDataRefresh;
        SetChatMessageLoggingEnabled(false);
        ActGlobals.oFormActMain.BeforeLogLineRead -= OFormActMain_BeforeLogLineRead;

        var started = Environment.TickCount64;
        var teardown = Task.Run(() =>
        {
            ffxivActPlugin.DeInitPlugin();
            ffxivActPlugin.Dispose();
        });
        var teardownClean = WaitTeardown(teardown);
        var remaining = (int)Math.Max(0, TeardownTimeoutMs - (Environment.TickCount64 - started));
        var scanStopped = scanThread.Join(remaining);

        StoppedCleanly = teardownClean && scanStopped;
        if (StoppedCleanly)
        {
            cancellationTokenSource.Dispose();
            Marshal.FreeHGlobal(mobData);
            return;
        }

        Plugin.Log.Information(
            $"[解析器關閉] {TeardownTimeoutMs / 1000} 秒內沒有乾淨停下（FFXIV_ACT_Plugin 關閉：" +
            $"{(teardown.IsCompleted ? teardown.IsFaulted ? "擲例外" : "完成" : "仍在執行")}；" +
            $"掃描執行緒：{(scanStopped ? "已結束" : $"仍在「{scanPhase}」")}）——不再等待，" +
            "共用記憶體區塊不釋放，避免還在跑的執行緒讀到已釋放的記憶體");
        if (!teardown.IsCompleted)
            teardown.ContinueWith(
                t => Plugin.Log.Information(
                    $"[解析器關閉] FFXIV_ACT_Plugin 在逾時後才{(t.IsFaulted ? "擲例外結束" : "完成")}，" +
                    $"距開始關閉 {(Environment.TickCount64 - started) / 1000.0:F1} 秒"),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static bool WaitTeardown(Task teardown)
    {
        try
        {
            return teardown.Wait(TeardownTimeoutMs);
        }
        catch (AggregateException ex)
        {
            Plugin.Log.Warning(ex.InnerException ?? ex, "[解析器關閉] FFXIV_ACT_Plugin 關閉時擲例外");
            return false;
        }
    }

    private Thread StartScanThread()
    {
        var thread = new Thread(() => ScanMemory(cancellationTokenSource.Token))
        {
            IsBackground = true,
            Name = "IINACT ScanMemory"
        };
        thread.Start();
        return thread;
    }

    /// <summary>框架執行緒呼叫。只在舊的掃描執行緒已經結束時才開新的，兩條同時跑會搶同一批處理器。</summary>
    public bool TryRestartScanThread()
    {
        if (disposed || cancellationTokenSource.IsCancellationRequested || scanThread.IsAlive)
            return false;
        scanExitReason = null;
        scanThread = StartScanThread();
        return true;
    }

    public ParserHealth GetHealth() => new(
        ScanThreadExited: !scanThread.IsAlive && !cancellationTokenSource.IsCancellationRequested,
        ScanExitReason: scanExitReason,
        ScanBusySince: Volatile.Read(ref scanBusySince),
        ScanPhase: scanPhase,
        PendingRefreshes: refreshSemaphore.CurrentCount,
        LastNetworkLineTicks: Volatile.Read(ref lastNetworkLineTicks),
        ParserLogQueueLength: parserLogQueue?.Count ?? -1);

    public void SetChatMessageLoggingEnabled(bool enabled)
    {
        chatGui.ChatMessage -= OnChatMessage;

        if (enabled)
            chatGui.ChatMessage += OnChatMessage;
    }

    private void SetupSettingsMediator()
    {
        settingsMediator = ffxivActPlugin._dataCollection._settingsMediator;

        DataCollectionSettings = new DataCollectionSettingsEventArgs
        {
            LogFileFolder = ActGlobals.oFormActMain.LogFilePath,
            RegionID = Region.TraditionalChinese,
            ProcessID = Environment.ProcessId
        };
        settingsMediator.DataCollectionSettings = DataCollectionSettings;

        ParseSettings = new ParseSettings
        {
            DisableDamageShield = configuration.DisableDamageShield,
            DisableCombinePets = configuration.DisableCombinePets,
            LanguageID = ClientLanguage,
            ParseFilter = (ParseFilterMode)configuration.ParseFilterMode,
            SimulateIndividualDoTCrits = configuration.SimulateIndividualDoTCrits,
            ShowRealDoTTicks = configuration.ShowRealDoTTicks,
            ShowDebug = configuration.ShowDebug,
            EnableBenchmarks = false
        };
        settingsMediator.ParseSettings = ParseSettings;

        settingsMediator.ProcessException = OnProcessException;

        var line = logFormat.FormatParseSettings(ParseSettings.DisableDamageShield, ParseSettings.DisableCombinePets,
                                                 ParseSettings.LanguageID, ParseSettings.ParseFilter,
                                                 ParseSettings.SimulateIndividualDoTCrits,
                                                 ParseSettings.ShowRealDoTTicks);
        logOutput.WriteLine(LogMessageType.Settings, DateTime.MinValue, line);

        var line2 = logFormat.FormatMemorySettings(DataCollectionSettings.ProcessID,
                                                   DataCollectionSettings.LogFileFolder,
                                                   DataCollectionSettings.LogAllNetworkData,
                                                   DataCollectionSettings.DisableCombatLog,
                                                   DataCollectionSettings.RegionID);
        logOutput.WriteLine(LogMessageType.Settings, DateTime.MinValue, line2);

        logOutput.CallMethod("ConfigureLogFile", null);
        ActGlobals.oFormActMain.GetDateTimeFromLog = parseMediator.ParseLogDateTime;

        if (!processManager.Verify())
            throw new InvalidOperationException("Game offsets could not be found");
    }

    private void OnChatMessage(
        XivChatType type, int senderId, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        var evenType = (uint)type;
        var player = sender.TextValue;
        var text = message.TextValue.Replace('\r', ' ').Replace('\n', ' ')
                                    .Replace('|', '❘');
        var line = logFormat.FormatChatMessage(evenType, player, text);

        logOutput.WriteLine(LogMessageType.ChatLog, GameServerTime.CurrentServerTime, line);
    }

    private void SetupActWrapper()
    {
        var actWrapper = logOutput.GetField<ACTWrapper>("_actWrapper");

        actWrapper.TimeStampLen = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture).Length + 3;
        actWrapper.LogPathHasCharName = false;
        actWrapper.OverrideMainFormVisible = true;

        ACT_UIMods.UpdateACTTables(false);

        ActGlobals.oFormActMain.FfxivPlugin = ffxivActPlugin;
    }

    private void OFormActMain_BeforeLogLineRead(bool isImport, LogLineEventArgs logInfo)
    {
        if (IsNetworkLine(logInfo.logLine))
            Volatile.Write(ref lastNetworkLineTicks, Environment.TickCount64);
        (logInfo.logLine, logInfo.detectedType) =
            parseMediator.BeforeLogLineRead(isImport, logInfo.detectedTime, logInfo.logLine);
    }

    // 只看原始行開頭「NN|」的型別碼：20～42 是 FFXIV_ACT_Plugin 從封包產生的行，40（換地圖）來自記憶體。
    internal static bool IsNetworkLine(string? line)
    {
        if (line is null || line.Length < 4) return false;
        var bar = line[2] == '|' ? 2 : line[3] == '|' ? 3 : -1;
        if (bar < 0) return false;
        if (!int.TryParse(line.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out var type))
            return false;
        return type is >= 20 and <= 42 && type != 40;
    }

    private void SetupDataSubscription()
    {
        ffxivActPlugin.DataSubscription.ZoneChanged += OnZoneChanged;
    }

    private static void OnZoneChanged(uint zoneId, string zoneName)
    {
        ActGlobals.oFormActMain.ChangeZone(zoneName);
    }

    private const long ProcessExceptionEchoMs = 60 * 1000;
    private const int ProcessExceptionEchoCap = 2048;

    private sealed class ProcessExceptionEcho
    {
        public long Seen;
        public long Suppressed;
        public long LastPrintedAt;
    }

    private static readonly ConcurrentDictionary<string, ProcessExceptionEcho> ProcessExceptionEchoes = new();

    /// <summary>
    /// FFXIV_ACT_Plugin 的解析執行緒直接呼叫，可能多條並行 —— 計數表必須並行安全，
    /// 而且鎖內只准算數字（Plugin.Log 是檔案 I/O，一律留到鎖外）。
    /// 逐字相同的訊息第一次照印，之後每 <see cref="ProcessExceptionEchoMs"/> 毫秒補一行帶計數的，
    /// 所以「有哪些訊息」與「還在不在發生」兩個訊號都保得住。
    /// </summary>
    private static void OnProcessException(DateTime timestamp, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (!ProcessExceptionEchoes.TryGetValue(text, out var echo))
        {
            // 上限只是防爆：滿了就退回照印不去重——寧可吵，也不要靜默漏掉訊息。
            if (ProcessExceptionEchoes.Count >= ProcessExceptionEchoCap)
            {
                Plugin.Log.Debug($"[FFXIV_ACT_Plugin] {text}");
                return;
            }

            echo = ProcessExceptionEchoes.GetOrAdd(text, _ => new ProcessExceptionEcho());
        }

        long repeats;
        lock (echo)
        {
            var now = Environment.TickCount64;
            if (echo.Seen++ == 0)
            {
                echo.LastPrintedAt = now;
                repeats = 0;
            }
            else
            {
                echo.Suppressed++;
                if (now - echo.LastPrintedAt < ProcessExceptionEchoMs) return;
                echo.LastPrintedAt = now;
                repeats = echo.Suppressed;
                echo.Suppressed = 0;
            }
        }

        Plugin.Log.Debug(repeats == 0
                             ? $"[FFXIV_ACT_Plugin] {text}"
                             : $"[FFXIV_ACT_Plugin]（{ProcessExceptionEchoMs / 1000} 秒內又重複 {repeats} 次）{text}");
    }

    [SuppressGCTransition]
    [LibraryImport("SafeMemoryReader.dll")]
    private static partial int ReadMemory(nint dest, nint src, int size);

    private unsafe void MobDataRefresh(IFramework _)
    {
        if (settingsMediator.DataCollectionSettings == null)
            return;

        if (mobDataAge < 3 || (!condition[ConditionFlag.BoundByDuty56] && mobDataAge < 10))
        {
            mobDataAge++;
            if (mobArrayProcessor.PrimaryPlayerPointer == nint.Zero)
                return;

            refreshSemaphore.Release();
            return;
        }

        mobDataAge = 0;
        var mobArrayAddress = (ulong*)mobArrayProcessor._readMobArray.Read64();
        var mobArray = mobArrayProcessor._internalMmobArray;

        if (*mobArrayAddress == 0)
        {
            Array.Clear(mobArray);
            return;
        }

        ReadMemory(mobDataOffsets[0], (nint)(void*)*mobArrayAddress, combatantSize);
        mobArray[0] = mobDataOffsets[0];

        for (var i = 1; i < mobArraySize; i++)
        {
            if (*(mobArrayAddress + i) == 0)
            {
                mobArray[i] = nint.Zero;
                continue;
            }
            ReadMemory(mobDataOffsets[i], (nint)(void*)*(mobArrayAddress + i), combatantSize);
            mobArray[i] = mobDataOffsets[i];
        }

        refreshSemaphore.Release();
    }

    private void ScanMemory(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                scanPhase = "waiting for a frame";
                refreshSemaphore.Wait(token);
                Volatile.Write(ref scanBusySince, Environment.TickCount64);
                try
                {
                    scanPhase = "server time";
                    serverTimeProcessor.ServerTime = GameServerTime.CurrentServerTime;

                    var zoneId = zoneMapProcessor.ZoneID;
                    scanPhase = "zone refresh";
                    zoneMapProcessor.Refresh();
                    if (zoneMapProcessor.ZoneID == 0)
                        continue;

                    if (zoneId != zoneMapProcessor.ZoneID)
                    {
                        scanPhase = "combatant rescan (zone changed)";
                        combatantManager.Rescan();
                    }
                    else
                    {
                        scanPhase = "combatant refresh";
                        combatantManager.Refresh();
                    }

                    scanPhase = "player refresh";
                    playerProcessor.Refresh();
                    scanPhase = "party refresh";
                    partyProcessor.Refresh();
                }
                finally
                {
                    Volatile.Write(ref scanBusySince, 0);
                }
            }
            catch (Exception ex) when (ex is ThreadAbortException or OperationCanceledException or ObjectDisposedException)
            {
                if (!token.IsCancellationRequested)
                    scanExitReason = $"{ex.GetType().Name}（在「{scanPhase}」）: {ex.Message}";
                return;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[FFXIV_ACT_Plugin] ScanMemory failure");
            }
        }
    }
}
