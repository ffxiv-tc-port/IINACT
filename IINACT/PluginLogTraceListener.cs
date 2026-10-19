using System.Diagnostics;

namespace IINACT;

public class PluginLogTraceListener : TraceListener
{
    public override void Write(string? message) { }

    public override void WriteLine(string? message) { }

    public override void WriteLine(string? message, string? category)
    {
        if (message is null) return;
        
        // FFXIV_ACT_Plugin 經由 Trace 送出來的內容，同一句也會走 DataSubscription
        // 的 ProcessException 以 Debug 再寫一次，所以這裡不再重複寫一份 Information。
        if (category?.Equals("machina", StringComparison.OrdinalIgnoreCase) ?? false)
            Plugin.Log.Information($"[MACHINA] {message}");
        
        if (category?.Equals("debug-machina", StringComparison.OrdinalIgnoreCase) ?? false)
            Plugin.Log.Debug($"[MACHINA] {message}");
    }
}
