namespace RainbowMage.OverlayPlugin
{
    // 對 LogLine 事件只需要原始那一行的接收者。EventDispatcher.DispatchLogLine 在還沒有人要 JObject 時
    // 改呼叫這個；結果必須與 HandleEvent 收到同一行的 LogLine JObject 完全相同。
    internal interface IRawLogLineReceiver : IEventReceiver
    {
        void HandleRawLogLine(string rawLine);
    }
}
