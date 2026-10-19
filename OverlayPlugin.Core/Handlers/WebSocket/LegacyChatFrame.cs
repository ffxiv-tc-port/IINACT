#nullable enable
using System;
using System.Buffers;
using System.Text;

namespace RainbowMage.OverlayPlugin.Handlers.WebSocket;

// LegacyHandler 收到 LogLine 時送出的 {"type":"broadcast","msgtype":"Chat","msg":<rawLine>}，直接寫成 UTF-8；
// 必須與 JObject.ToString(Formatting.None) 再 Encoding.UTF8.GetBytes 逐位元組相同（跳脫規則照 Newtonsoft 預設）。
internal static class LegacyChatFrame
{
    private const string Prefix = "{\"type\":\"broadcast\",\"msgtype\":\"Chat\",\"msg\":";

    // 回傳的陣列租自 ArrayPool<byte>.Shared，前 count 個位元組是整個框；呼叫端用完必須 Return。
    // 刻意不用 [ThreadStatic] 快取：呼叫端執行緒不保證在插件卸載前結束，ALC 回收後它才退出會寫壞 GC 堆（dotnet/runtime#135441）。
    public static byte[] EncodeRented(string? rawLine, out int count)
    {
        var chars = ArrayPool<char>.Shared.Rent(Prefix.Length + 2 + (rawLine?.Length ?? 4) * 6 + 1);
        try
        {
            var n = WriteFrame(chars, rawLine);
            var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(n));
            count = Encoding.UTF8.GetBytes(chars.AsSpan(0, n), bytes);
            return bytes;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private static int WriteFrame(char[] chars, string? rawLine)
    {
        Prefix.AsSpan().CopyTo(chars);
        var n = Prefix.Length;
        if (rawLine == null)
        {
            "null".AsSpan().CopyTo(chars.AsSpan(n));
            n += 4;
        }
        else
        {
            chars[n++] = '"';
            foreach (var c in rawLine)
                n = WriteEscaped(chars, n, c);
            chars[n++] = '"';
        }

        chars[n++] = '}';
        return n;
    }

    internal static int WriteEscaped(char[] buf, int n, char c)
    {
        switch (c)
        {
            case '"':
                buf[n++] = '\\';
                buf[n++] = '"';
                return n;
            case '\\':
                buf[n++] = '\\';
                buf[n++] = '\\';
                return n;
            case '\b':
                buf[n++] = '\\';
                buf[n++] = 'b';
                return n;
            case '\f':
                buf[n++] = '\\';
                buf[n++] = 'f';
                return n;
            case '\n':
                buf[n++] = '\\';
                buf[n++] = 'n';
                return n;
            case '\r':
                buf[n++] = '\\';
                buf[n++] = 'r';
                return n;
            case '\t':
                buf[n++] = '\\';
                buf[n++] = 't';
                return n;
            case (char)0x85:
            case (char)0x2028:
            case (char)0x2029:
                return WriteHexEscape(buf, n, c);
            default:
                if (c < ' ')
                    return WriteHexEscape(buf, n, c);
                buf[n++] = c;
                return n;
        }
    }

    // 反斜線、u、四位小寫十六進位。
    private static int WriteHexEscape(char[] buf, int n, char c)
    {
        buf[n++] = '\\';
        buf[n++] = 'u';
        for (var shift = 12; shift >= 0; shift -= 4)
        {
            var d = (c >> shift) & 0xF;
            buf[n++] = (char)(d < 10 ? '0' + d : 'a' + d - 10);
        }

        return n;
    }
}
