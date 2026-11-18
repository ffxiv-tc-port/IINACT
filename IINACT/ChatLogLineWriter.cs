using System.Globalization;
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
/// The shortcut is used only if a self-test at construction matched <see cref="ILogFormat.FormatChatMessage"/> of the
/// loaded FFXIV_ACT_Plugin on sample inputs; otherwise every message goes through <see cref="WriteOriginal"/>.
/// Called from the game's chat print hook; the buffer is guarded by <see cref="gate"/> regardless.
/// </remarks>
internal sealed class ChatLogLineWriter
{
    private readonly ILogFormat logFormat;
    private readonly ILogOutput logOutput;
    private readonly object gate = new();
    private char[] chars = new char[256];
    private int length;

    public ChatLogLineWriter(ILogFormat logFormat, ILogOutput logOutput)
    {
        this.logFormat = logFormat;
        this.logOutput = logOutput;
        FastFormat = SelfTestFormat();
    }

    /// <summary>False＝the loaded FFXIV_ACT_Plugin formats chat lines differently; every message uses the original path.</summary>
    public bool FastFormat { get; }

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
            length = 0;
            AppendLine((uint)type, sender, message);
            line = new string(chars, 0, length);
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
            {
                Reserve(s.Length);
                s.CopyTo(chars.AsSpan(length));
                length += s.Length;
            }
        }
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
            new(new TextPayload("a|b\r\nc\0"), NewLinePayload.Payload, new TextPayload("❘末😀")),
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
                    length = 0;
                    AppendLine((uint)type, sender, message);
                    if (!chars.AsSpan(0, length).SequenceEqual(FormatOriginal(type, sender, message)))
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
}
