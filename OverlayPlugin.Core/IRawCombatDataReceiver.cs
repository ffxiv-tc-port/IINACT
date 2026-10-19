using RainbowMage.OverlayPlugin.EventSources;

namespace RainbowMage.OverlayPlugin
{
    // 對 CombatData 只需要序列化結果的接收者。EventDispatcher.DispatchCombatData 在還沒有人要 JObject 時改呼叫這個；
    // 送出的內容必須與 HandleEvent 收到同一份 CombatData JObject 時完全相同。data 的緩衝區只在這次呼叫內有效。
    internal interface IRawCombatDataReceiver : IEventReceiver
    {
        void HandleRawCombatData(CombatDataSnapshot data);
    }
}
