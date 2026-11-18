#nullable enable
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace RainbowMage.OverlayPlugin.Handlers.Ipc;

internal class LegacyIpcHandler : LegacyHandler
{
    private ICallGateProvider<JObject, bool> Receiver { get; }
    private ICallGateSubscriber<JObject, bool> Sender { get; }
    private FrameworkSendQueue Outbox { get; }

    public LegacyIpcHandler(
        string name, ICallGateProvider<JObject, bool> receiver, ICallGateSubscriber<JObject, bool> sender,
        ILogger logger, EventDispatcher eventDispatcher, FFXIVRepository repository, IFramework framework) : base(
        name, logger, eventDispatcher, repository)
    {
        Receiver = receiver;
        Sender = sender;
        Outbox = new FrameworkSendQueue(
            e => Sender.InvokeFunc(e), a => framework.Run(a), () => framework.IsInFrameworkUpdateThread,
            ex => Logger.Log(LogLevel.Error, $"IPC send to {name} failed: {ex}"));
        
        Receiver.RegisterAction(DataReceived);

        // 建構失敗時呼叫端拿不到這個物件、無從處置：先退訂再重擲，不留孤兒訂閱。
        try
        {
            Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    protected override void Send(JObject e) => Outbox.Post(e);

    // 在對方呼叫 CreateLegacySubscriber 的那一次呼叫裡就地送完：對方還沒註冊接收端就照舊建立失敗。
    protected override void SendInitial(JObject e) => Outbox.PostAndFlush(e);
    
    public override void Dispose()
    {
        Receiver.UnregisterAction();
        base.Dispose();
        Outbox.Close();
    }
}

