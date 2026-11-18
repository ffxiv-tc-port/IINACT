using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIV_ACT_Plugin.Common;
using FFXIV_ACT_Plugin.Logfile;
using IINACT.Network;

namespace IINACT;

/// <summary>
/// Writes a chat message as the ACT "00" line, byte for byte what <see cref="WriteOriginal"/> writes, but builds the
/// line in a reused buffer instead of the intermediate strings (sender/message text, replaced copies, format args).
/// </summary>
/// <remarks>
/// Each shortcut is used only if a self-test at construction matched the loaded FFXIV_ACT_Plugin on sample inputs:
/// <see cref="FastFormat"/> against <see cref="ILogFormat.FormatChatMessage"/>, <see cref="DirectWrite"/> against
/// <see cref="LogOutput.WriteLine"/> on two scratch instances. Called from the game's chat print hook; the buffers
/// are guarded by <see cref="gate"/> regardless.
/// </remarks>
internal sealed class ChatLogLineWriter
{
    // Room in front of the line for "NN|{serverDate:O}|", which is only known after the line is built.
    private const int PrefixRoom = 64;

    private readonly ILogFormat logFormat;
    private readonly ILogOutput logOutput;
    private readonly LogOutput? directOutput;
    private readonly string typeField;
    private readonly object gate = new();
    private char[] chars = new char[256];
    private byte[] utf8 = new byte[512];
    private int length;

    public ChatLogLineWriter(ILogFormat logFormat, ILogOutput logOutput)
    {
        this.logFormat = logFormat;
        this.logOutput = logOutput;
        typeField = ((int)LogMessageType.ChatLog).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
        FastFormat = SelfTestFormat();
        if (FastFormat && logOutput is LogOutput output && SelfTestDirect())
            directOutput = output;
    }

    /// <summary>False＝the loaded FFXIV_ACT_Plugin formats chat lines differently; every message uses the original path.</summary>
    public bool FastFormat { get; }

    /// <summary>False＝lines go through <see cref="ILogOutput.WriteLine"/> (it builds several more strings per line).</summary>
    public bool DirectWrite => directOutput != null;

    public void Write(XivChatType type, SeString sender, SeString message)
    {
        if (!FastFormat)
        {
            WriteOriginal(type, sender, message);
            return;
        }

        string line;
        lock (gate)
        {
            length = PrefixRoom;
            AppendLine((uint)type, sender, message);
            if (directOutput != null)
            {
                EnqueueDirect(directOutput, GameServerTime.CurrentServerTime);
                return;
            }

            line = new string(chars, PrefixRoom, length - PrefixRoom);
        }

        logOutput.WriteLine(LogMessageType.ChatLog, GameServerTime.CurrentServerTime, line);
    }

    private void WriteOriginal(XivChatType type, SeString sender, SeString message) =>
        logOutput.WriteLine(LogMessageType.ChatLog, GameServerTime.CurrentServerTime, FormatOriginal(type, sender, message));

    private string FormatOriginal(XivChatType type, SeString sender, SeString message)
    {
        var evenType = (uint)type;
        // 戰鬥訊息沒有發話者；空清單的 TextValue 就是空字串，不必走一趟 LINQ＋StringBuilder。
        var player = sender.Payloads.Count == 0 ? string.Empty : sender.TextValue;
        var text = message.TextValue.Replace('\r', ' ').Replace('\n', ' ')
                                    .Replace('|', '❘');
        return logFormat.FormatChatMessage(evenType, player, text);
    }

    // Same text as "{type:X4}|{sender.TextValue}|{message.TextValue with \r \n → ' ' and | → ❘}".
    private void AppendLine(uint type, SeString sender, SeString message)
    {
        Reserve(8);
        type.TryFormat(chars.AsSpan(length), out var written, "X4", CultureInfo.InvariantCulture);
        length += written;
        Append('|');
        AppendText(sender);
        Append('|');
        var start = length;
        AppendText(message);
        for (var i = start; i < length; i++)
        {
            switch (chars[i])
            {
                case '\r' or '\n':
                    chars[i] = ' ';
                    break;
                case '|':
                    chars[i] = '❘';
                    break;
            }
        }
    }

    // SeString.TextValue concatenates the Text of every ITextProvider payload in order (null appends nothing).
    private void AppendText(SeString text)
    {
        foreach (var payload in text.Payloads)
        {
            if (payload is ITextProvider provider && provider.Text is { } s)
                Append(s);
        }
    }

    private void Append(string s)
    {
        Reserve(s.Length);
        s.CopyTo(chars.AsSpan(length));
        length += s.Length;
    }

    private void Append(char c)
    {
        Reserve(1);
        chars[length++] = c;
    }

    private void Reserve(int extra)
    {
        if (length + extra <= chars.Length) return;
        Array.Resize(ref chars, Math.Max(chars.Length * 2, length + extra));
    }

    // LogOutput.WriteLine for the line in chars[PrefixRoom..length), same locks and order:
    // text = "{type:00}|{serverDate:O}|{line, NUL → ' '}", entry = text + "|" + u_65535(text + "|" + ++_logIndex).
    private void EnqueueDirect(LogOutput output, DateTime serverDate)
    {
        if (serverDate == DateTime.MinValue)
        {
            // That branch reads and seeds _lastLogTime; never taken for chat lines, left to the original.
            output.WriteLine(LogMessageType.ChatLog, serverDate, new string(chars, PrefixRoom, length - PrefixRoom));
            return;
        }

        lock (output._lastLogTimeLock)
            output._lastLogTime = serverDate;

        chars.AsSpan(PrefixRoom, length - PrefixRoom).Replace('\0', ' ');
        Span<char> head = stackalloc char[PrefixRoom];
        typeField.CopyTo(head);
        var headLength = typeField.Length;
        head[headLength++] = '|';
        if (!serverDate.TryFormat(head[headLength..], out var dateLength, "O", CultureInfo.InvariantCulture)
            || headLength + dateLength + 1 > PrefixRoom)
        {
            output.WriteLine(LogMessageType.ChatLog, serverDate, new string(chars, PrefixRoom, length - PrefixRoom));
            return;
        }

        headLength += dateLength;
        head[headLength++] = '|';
        var start = PrefixRoom - headLength;
        head[..headLength].CopyTo(chars.AsSpan(start));
        Reserve(32);
        var end = length;

        lock (output._LogQueueLock)
        {
            var index = Interlocked.Increment(ref output._logIndex);
            chars[end] = '|';
            index.TryFormat(chars.AsSpan(end + 1), out var digits, default, CultureInfo.InvariantCulture);
            int hashLength;
            if (output._disableHash)
            {
                chars[end + 1] = '0';
                hashLength = 1;
            }
            else
            {
                var input = chars.AsSpan(start, end + 1 + digits - start);
                var byteCount = Encoding.UTF8.GetByteCount(input);
                if (utf8.Length < byteCount)
                    utf8 = new byte[Math.Max(utf8.Length * 2, byteCount)];
                Encoding.UTF8.GetBytes(input, utf8);
                Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
                SHA256.HashData(utf8.AsSpan(0, byteCount), digest);
                Convert.TryToHexStringLower(digest[..8], chars.AsSpan(end + 1, 16), out hashLength);
            }

            output._LogQueue.Enqueue(new string(chars, start, end + 1 + hashLength - start));
        }
    }

    private bool SelfTestFormat()
    {
        SeString[] senders =
        [
            new(),
            new(new UIForegroundPayload(1), new TextPayload("Tester"), RawPayload.LinkTerminator),
        ];
        SeString[] messages =
        [
            new(new TextPayload("你對"), new UIForegroundPayload(511), new TextPayload("深宮的石像"), UIForegroundPayload.UIForegroundOff,
                new TextPayload("造成了12345點傷害。")),
            new(new TextPayload("a|b\r\nc\0"), NewLinePayload.Payload, new TextPayload("❘末" + char.ConvertFromUtf32(0x1F600))),
            new(),
        ];
        XivChatType[] types = [(XivChatType)0x082B, XivChatType.Say, (XivChatType)0xFFFF, 0];
        try
        {
            lock (gate)
            {
                foreach (var type in types)
                foreach (var sender in senders)
                foreach (var message in messages)
                {
                    length = PrefixRoom;
                    AppendLine((uint)type, sender, message);
                    if (!chars.AsSpan(PrefixRoom, length - PrefixRoom).SequenceEqual(FormatOriginal(type, sender, message)))
                        return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool SelfTestDirect()
    {
        DateTime[] times =
        [
            new DateTime(2026, 11, 18, 17, 7, 47, DateTimeKind.Local).AddTicks(1234567),
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1),
        ];
        string[] lines =
        [
            "082B||你對深宮的石像造成了12345點傷害。",
            "000A|Tester|a❘b  c\0d",
            "0039||" + char.ConvertFromUtf32(0x1F600) + (char)0xD800 + "z",
            new string('長', 300),
            "",
        ];
        try
        {
            var reference = new LogOutput(null!, null!, null!);
            var mine = new LogOutput(null!, null!, null!);
            foreach (var disableHash in new[] { false, true })
            {
                reference._disableHash = mine._disableHash = disableHash;
                foreach (var time in times)
                foreach (var line in lines)
                {
                    reference.WriteLine(LogMessageType.ChatLog, time, line);
                    lock (gate)
                    {
                        length = PrefixRoom;
                        Append(line);
                        EnqueueDirect(mine, time);
                    }
                }
            }

            return reference._LogQueue.SequenceEqual(mine._LogQueue)
                   && reference._logIndex == mine._logIndex
                   && reference._lastLogTime == mine._lastLogTime;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
