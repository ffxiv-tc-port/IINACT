#nullable enable

using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NetCoreServer;
using RainbowMage.OverlayPlugin.Handlers.WebSocket;

namespace RainbowMage.OverlayPlugin.WebSocket;

// 這些覆寫跑在 NetCoreServer 的 IOCP 回呼上，它只在同步路徑接 ObjectDisposedException；
// 漏出去的例外＝未處理例外、整個遊戲結束 ⇒ 每個覆寫自己整段接住。
internal class OverlaySession : WsSession
{
    private static int callbackFailureLogged;

    private EventDispatcher Dispatcher { get; }
    private FFXIVRepository Repository { get; }
    private ILogger Logger { get; }
    private ISocketHandler? Handler { get; set; }

    public OverlaySession(WsServer server, TinyIoCContainer container) : base(server)
    {
        Dispatcher = container.Resolve<EventDispatcher>();
        Repository = container.Resolve<FFXIVRepository>();
        Logger = container.Resolve<ILogger>();
    }

    public override void OnWsConnected(HttpRequest request)
    {
        try
        {
            Logger.Log(LogLevel.Debug, $"Overlay WebSocket session with Id {Id} connected!");

            switch (request.Url)
            {
                case "/ws":
                    Handler = new SocketHandler(Logger, Dispatcher, this);
                    break;
                case "/MiniParse":
                case "/BeforeLogLineRead":
                    Handler = new LegacySocketHandler(Logger, Dispatcher, Repository, this);
                    break;
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            ReportCallbackFailure(nameof(OnWsConnected), ex);
        }
    }

    public override void OnWsDisconnected()
    {
        try
        {
            Logger.Log(LogLevel.Debug, $"Overlay WebSocket session with Id {Id} disconnected!");
            Handler?.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            ReportCallbackFailure(nameof(OnWsDisconnected), ex);
        }
    }

    public override void OnWsReceived(byte[] buffer, long offset, long size)
    {
        try
        {
            var message = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
#if DEBUG
            Logger.Log(LogLevel.Trace, $"Overlay WebSocket {Id} received message: {message}");
#endif
            Handler?.OnMessage(message);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            ReportCallbackFailure(nameof(OnWsReceived), ex);
        }
    }

    protected override void OnError(SocketError error)
    {
        try
        {
            Handler?.OnError(error);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            ReportCallbackFailure(nameof(OnError), ex);
        }
    }

    // ObjectDisposedException 不算失敗（連線正在關閉）；其餘整個插件生命週期只記第一次，連線照常繼續。
    private void ReportCallbackFailure(string callback, Exception ex)
    {
        if (Interlocked.Exchange(ref callbackFailureLogged, 1) != 0)
            return;
        try
        {
            Logger.Log(LogLevel.Error,
                       $"Overlay WebSocket {callback} failed; later failures in WebSocket callbacks are not logged: {ex}");
        }
        catch { }
    }
}
    
