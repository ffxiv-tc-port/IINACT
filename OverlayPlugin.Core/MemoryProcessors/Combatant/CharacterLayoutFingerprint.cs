using System;
using Dalamud.Game;

namespace RainbowMage.OverlayPlugin.MemoryProcessors.Combatant
{
    public enum CharacterLayout
    {
        Unknown = 0,
        Gen73,
        Gen74,
    }

    // 從遊戲碼判斷 BattleChara 版面世代：GetCastInfo／GetStatusManager 是 `lea rax,[rcx+disp32]; ret`，
    // disp32 就是 CastInfo／StatusManager 的偏移（CombatantMemory73 的 IsCasting1／Effects-8，74 各 +0x10）。
    // 只認「一個世代齊全、另一個世代完全沒有」；兩邊都有或都缺一律 Unknown，不猜。
    public static class CharacterLayoutFingerprint
    {
        public const string Cast73 = "48 8D 81 80 27 00 00 C3";
        public const string Status73 = "48 8D 81 A0 23 00 00 C3";
        public const string Cast74 = "48 8D 81 90 27 00 00 C3";
        public const string Status74 = "48 8D 81 B0 23 00 00 C3";

        public static CharacterLayout Detect(ISigScanner scanner, out string detail)
        {
            return Detect(sig => scanner.ScanAllText(sig).Length, out detail);
        }

        // countInText：給 Dalamud 格式的特徵碼，回傳它在遊戲 .text 的命中數。
        public static CharacterLayout Detect(Func<string, int> countInText, out string detail)
        {
            int cast73 = countInText(Cast73);
            int status73 = countInText(Status73);
            int cast74 = countInText(Cast74);
            int status74 = countInText(Status74);
            detail = $"CastInfo 0x2780={cast73} 0x2790={cast74}, StatusManager 0x23A0={status73} 0x23B0={status74}";

            bool has73 = cast73 > 0 && status73 > 0;
            bool has74 = cast74 > 0 && status74 > 0;
            bool any73 = cast73 > 0 || status73 > 0;
            bool any74 = cast74 > 0 || status74 > 0;
            if (has73 && !any74)
                return CharacterLayout.Gen73;
            if (has74 && !any73)
                return CharacterLayout.Gen74;
            return CharacterLayout.Unknown;
        }
    }
}
