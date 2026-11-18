using System.Threading;

namespace RainbowMage.OverlayPlugin.EventSources
{
    // 懸浮視窗 CombatData 收集的累計次數與戰鬥者數，給 IINACT 每小時一行的計數用。
    // 收集在計時器（執行緒池）上跑，讀取端在框架執行緒，所以一律 Interlocked；累加不配置。
    public static class CombatDataCollectStats
    {
        private static long collects;
        private static long combatants;

        internal static void Record(int combatantCount)
        {
            Interlocked.Increment(ref collects);
            Interlocked.Add(ref combatants, combatantCount);
        }

        // 兩個值各自歸零；與同時進行的 Record 交錯時最多差一次收集，計數用途可接受。
        public static void Take(out long collectCount, out long combatantCount)
        {
            collectCount = Interlocked.Exchange(ref collects, 0);
            combatantCount = Interlocked.Exchange(ref combatants, 0);
        }
    }
}
