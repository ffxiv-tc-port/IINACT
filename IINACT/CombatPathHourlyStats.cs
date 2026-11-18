using System.Diagnostics;
using System.Globalization;
using Dalamud.Plugin.Services;
using IINACT.Network;
using RainbowMage.OverlayPlugin.EventSources;

namespace IINACT;

// 本檔契約：Tick 只在框架執行緒跑、每秒看一次時間；整點以外不組字串、不配置。
// 每個窗寫一行 Information（窗內懸浮視窗收集與封包入列都是 0 就不寫），卸載時補寫未滿的那一窗。
// 窗以行程啟動時間為錨、每小時一格：熱更新後不漂移，也與 Dalamud [PerfDiag] 的逐小時窗大致對齊。
public sealed class CombatPathHourlyStats
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly DateTime anchor;
    private DateTime from;
    private DateTime windowEnd;
    private long nextCheck;

    public CombatPathHourlyStats()
    {
        var now = DateTime.Now;
        try
        {
            using var self = Process.GetCurrentProcess();
            anchor = self.StartTime;
        }
        catch (Exception)
        {
            anchor = now;
        }

        if (anchor > now)
            anchor = now;
        from = now;
        windowEnd = WindowEndAfter(now);
    }

    public void Tick(IFramework _)
    {
        var tick = Environment.TickCount64;
        if (tick < nextCheck)
            return;
        nextCheck = tick + 1000;

        var now = DateTime.Now;
        if (now < windowEnd)
            return;
        Flush(windowEnd, false);
        from = windowEnd;
        windowEnd = WindowEndAfter(now);
    }

    public void FlushOnUnload() => Flush(DateTime.Now, true);

    private DateTime WindowEndAfter(DateTime now)
    {
        var elapsed = Math.Max(0, (now - anchor).Ticks);
        return anchor + TimeSpan.FromTicks((elapsed / Window.Ticks + 1) * Window.Ticks);
    }

    private void Flush(DateTime to, bool unload)
    {
        CombatDataCollectStats.Take(out var collects, out var combatants);
        ZoneDownHookManager.TakeEnqueueStats(out var packets, out var bytes);
        if (collects == 0 && packets == 0)
            return;

        var avg = collects == 0 ? 0.0 : (double)combatants / collects;
        Plugin.Log.Information(string.Create(CultureInfo.InvariantCulture,
            $"[CombatPathStats] from={from:yyyy-MM-dd HH:mm:ss} to={to:yyyy-MM-dd HH:mm:ss}{(unload ? " unload" : "")} " +
            $"collects={collects} combatants={combatants} avgCombatants={avg:0.00} packets={packets} packetBytes={bytes}"));
    }
}
