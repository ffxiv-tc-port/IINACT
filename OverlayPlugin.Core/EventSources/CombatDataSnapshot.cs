#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Advanced_Combat_Tracker;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin.Handlers.WebSocket;

namespace RainbowMage.OverlayPlugin.EventSources;

// MiniParseEventSource 一次更新收集到的 CombatData（已排序）。ToJObject 就是原本 CreateCombatData 組 JObject 的那段；
// Payload／LegacyFrame 直接寫成 UTF-8，必須與 ToJObject().ToString(Formatting.None)（以及 LegacyHandler 包成 broadcast 後）
// 再 Encoding.UTF8.GetBytes 逐位元組相同。只在 CanWriteDirect 時可用；同一次派送內只寫一次，ReleaseFrame 之後失效。
// 不是執行緒安全：只在派送它的那條執行緒上用。
internal sealed class CombatDataSnapshot
{
    private const string LegacyPrefix = "{\"type\":\"broadcast\",\"msgtype\":\"CombatData\",\"msg\":";

    // 上一次寫出的字元數，下一次直接借這麼大的緩衝區，省掉逐次加倍。
    private static int lastCharCount = 16 * 1024;

    private readonly Dictionary<string, string> encounter;
    private readonly List<KeyValuePair<CombatantData, Dictionary<string, string>>> combatant;
    private readonly string isActive;

    private byte[]? frame;
    private int frameLength;

    public CombatDataSnapshot(
        Dictionary<string, string> encounter, List<KeyValuePair<CombatantData, Dictionary<string, string>>> combatant,
        string isActive)
    {
        this.encounter = encounter;
        this.combatant = combatant;
        this.isActive = isActive;
        CanWriteDirect = ComputeCanWriteDirect();
    }

    // 為假時一律走 ToJObject，連擲出的例外都與原本相同：值或名字是 null、名字重複（JObject 會原位覆蓋）、
    // 有人設了 JsonConvert.DefaultSettings（JObject.FromObject 會吃它）。
    public bool CanWriteDirect { get; }

    public ReadOnlySpan<byte> Payload
    {
        get
        {
            EnsureFrame();
            return frame!.AsSpan(LegacyPrefix.Length, frameLength - LegacyPrefix.Length - 1);
        }
    }

    public ReadOnlySpan<byte> LegacyFrame
    {
        get
        {
            EnsureFrame();
            return frame!.AsSpan(0, frameLength);
        }
    }

    public JObject ToJObject()
    {
        JObject obj = new JObject();

        obj["type"] = "CombatData";
        obj["Encounter"] = JObject.FromObject(encounter);
        obj["Combatant"] = new JObject();

        foreach (var pair in combatant)
        {
            JObject value = new JObject();
            foreach (var pair2 in pair.Value)
            {
                value.Add(pair2.Key, Util.ReplaceNaNString(pair2.Value, "---"));
            }

            obj["Combatant"]![pair.Key.Name] = value;
        }

        obj["isActive"] = isActive;
        return obj;
    }

    public void ReleaseFrame()
    {
        if (frame == null) return;
        ArrayPool<byte>.Shared.Return(frame);
        frame = null;
    }

    private bool ComputeCanWriteDirect()
    {
        if (JsonConvert.DefaultSettings != null) return false;
        foreach (var kv in encounter)
            if (kv.Value == null) return false;
        for (var i = 0; i < combatant.Count; i++)
        {
            var name = combatant[i].Key.Name;
            if (name == null) return false;
            for (var j = 0; j < i; j++)
                if (string.Equals(combatant[j].Key.Name, name, StringComparison.Ordinal)) return false;
            foreach (var kv in combatant[i].Value)
                if (kv.Value == null) return false;
        }

        return true;
    }

    private void EnsureFrame()
    {
        if (frame != null) return;
        if (!CanWriteDirect) throw new InvalidOperationException("CombatData cannot be written directly");

        var chars = ArrayPool<char>.Shared.Rent(Math.Max(lastCharCount, 256));
        try
        {
            var n = 0;
            Raw(ref chars, ref n, LegacyPrefix);
            Raw(ref chars, ref n, "{\"type\":\"CombatData\",\"Encounter\":{");
            var first = true;
            foreach (var kv in encounter)
            {
                if (!first) Raw(ref chars, ref n, ",");
                first = false;
                Str(ref chars, ref n, kv.Key);
                Raw(ref chars, ref n, ":");
                Str(ref chars, ref n, kv.Value);
            }

            Raw(ref chars, ref n, "},\"Combatant\":{");
            for (var i = 0; i < combatant.Count; i++)
            {
                if (i > 0) Raw(ref chars, ref n, ",");
                Str(ref chars, ref n, combatant[i].Key.Name);
                Raw(ref chars, ref n, ":{");
                first = true;
                foreach (var kv in combatant[i].Value)
                {
                    if (!first) Raw(ref chars, ref n, ",");
                    first = false;
                    Str(ref chars, ref n, kv.Key);
                    Raw(ref chars, ref n, ":");
                    Str(ref chars, ref n, Util.ReplaceNaNString(kv.Value, "---"));
                }

                Raw(ref chars, ref n, "}");
            }

            Raw(ref chars, ref n, "},\"isActive\":");
            Str(ref chars, ref n, isActive);
            Raw(ref chars, ref n, "}}");

            lastCharCount = n;
            var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(n));
            frameLength = Encoding.UTF8.GetBytes(chars.AsSpan(0, n), bytes);
            frame = bytes;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private static void Raw(ref char[] buf, ref int n, string s)
    {
        Ensure(ref buf, n, s.Length);
        s.AsSpan().CopyTo(buf.AsSpan(n));
        n += s.Length;
    }

    private static void Str(ref char[] buf, ref int n, string s)
    {
        Ensure(ref buf, n, 2 + s.Length * 6);
        var a = buf;
        var i = n;
        a[i++] = '"';
        foreach (var c in s)
            i = LegacyChatFrame.WriteEscaped(a, i, c);
        a[i++] = '"';
        n = i;
    }

    private static void Ensure(ref char[] buf, int n, int more)
    {
        if (n + more <= buf.Length) return;
        var bigger = ArrayPool<char>.Shared.Rent(Math.Max(buf.Length * 2, n + more));
        buf.AsSpan(0, n).CopyTo(bigger);
        ArrayPool<char>.Shared.Return(buf);
        buf = bigger;
    }
}
