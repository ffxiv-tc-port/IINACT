#nullable enable
using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace RainbowMage.OverlayPlugin.Handlers.Ipc;

internal class IpcHandler : Handler
{
    private ICallGateProvider<JObject, bool> Receiver { get; }
    private ICallGateSubscriber<JObject, bool> Sender { get; }
    private FrameworkSendQueue Outbox { get; }

    public IpcHandler(string name, ICallGateProvider<JObject, bool> receiver, ICallGateSubscriber<JObject, bool> sender,
                      ILogger logger, EventDispatcher eventDispatcher, IFramework framework) : base(name, logger, eventDispatcher)
    {
        Receiver = receiver;
        Sender = sender;
        Outbox = new FrameworkSendQueue(
            e => Sender.InvokeFunc(e), a => framework.Run(a), () => framework.IsInFrameworkUpdateThread,
            ex => Logger.Log(LogLevel.Error, $"IPC send to {name} failed: {ex}"));
        
        Receiver.RegisterAction(DataReceived);
    }

    protected override void Send(JObject e) => Outbox.Post(e);
    
    public override void Dispose()
    {
        Receiver.UnregisterAction();
        base.Dispose();
        Outbox.Close();
    }
}
