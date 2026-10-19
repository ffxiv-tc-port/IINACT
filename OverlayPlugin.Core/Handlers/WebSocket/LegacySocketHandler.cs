#nullable enable
using System;
using System.Buffers;
using System.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin.EventSources;
using RainbowMage.OverlayPlugin.WebSocket;

namespace RainbowMage.OverlayPlugin.Handlers.WebSocket;

internal class LegacySocketHandler : LegacyHandler, ISocketHandler, IRawLogLineReceiver, IRawCombatDataReceiver
{
    private OverlaySession Session { get; }

    public LegacySocketHandler(
        ILogger logger, EventDispatcher eventDispatcher, FFXIVRepository repository, OverlaySession session) : base(
        "WSLegacyHandler", logger, eventDispatcher, repository)
    {
        Session = session;

        Start();
    }
    
    protected override void Send(JObject data) => Session.SendTextAsync(data.ToString(Formatting.None));

    // 與 HandleEvent 收到 LogLine 時送出的位元組相同，只是不經 JObject 與中間字串。
    public void HandleRawLogLine(string rawLine)
    {
        var frame = LegacyChatFrame.EncodeRented(rawLine, out var count);
        try
        {
            Session.SendTextAsync(frame.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    // 與 HandleEvent 收到 CombatData 時送出的位元組相同。
    public void HandleRawCombatData(CombatDataSnapshot data) => Session.SendTextAsync(data.LegacyFrame);
    

    public void OnError(SocketError error)
    {
        Logger.Log(LogLevel.Error, "Failed to send legacy WS message: {0}", error);
        Dispose();
    }

    public void OnMessage(string message)
    {
        JObject data;

        try
        {
            data = JObject.Parse(message);
        }
        catch (JsonException ex)
        {
            Logger.Log(LogLevel.Error, Resources.WSInvalidDataRecv, ex, message);
            return;
        }

        DataReceived(data);
    }
}

