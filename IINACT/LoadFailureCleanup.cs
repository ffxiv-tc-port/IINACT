namespace IINACT;

internal static class LoadFailureCleanup
{
    // 建構子擲例外時 Dalamud 不呼叫 Dispose，範圍服務只回收「當下已建出」的 hook；
    // 背景晚一步才建好並啟用的 hook 會留在遊戲函式上，所以由建構子自己收。
    // 契約：永不擲例外（呼叫端要原樣重擲原本的例外）；背景失敗＝沒有東西要收。
    internal static void DisposeWhenReady<T>(Task<T> pending, int waitMs, Action<string> warn) where T : class, IDisposable
    {
        try
        {
            if (pending.Wait(waitMs))
            {
                pending.Result.Dispose();
                return;
            }
        }
        catch (Exception e)
        {
            Warn(warn, pending.IsFaulted
                           ? $"[載入失敗收尾] 背景工作本身也失敗了，沒有東西要收回：{pending.Exception?.GetBaseException().Message}"
                           : $"[載入失敗收尾] 收回背景建立的物件時出錯：{e}");
            return;
        }

        Warn(warn, $"[載入失敗收尾] 背景工作 {waitMs} ms 內沒完成，改在它完成時收回。");
        pending.ContinueWith(t =>
        {
            try
            {
                if (t.IsCompletedSuccessfully)
                    t.Result.Dispose();
            }
            catch (Exception e)
            {
                Warn(warn, $"[載入失敗收尾] 收回背景建立的物件時出錯：{e}");
            }
        }, TaskScheduler.Default);
    }

    private static void Warn(Action<string> warn, string message)
    {
        try
        {
            warn(message);
        }
        catch
        {
            // 刻意吞掉：記錄失敗不能蓋掉呼叫端要重擲的原始例外。
        }
    }
}
