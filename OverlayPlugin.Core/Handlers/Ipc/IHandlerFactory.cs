using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace RainbowMage.OverlayPlugin.Handlers.Ipc;

internal interface IHandlerFactory
{
    IHandler Create(string name, TinyIoCContainer container)
    {
        var pluginInterface = container.Resolve<IDalamudPluginInterface>();
        var receiver = pluginInterface.GetIpcProvider<JObject, bool>($"IINACT.IpcProvider.{name}");
        var sender = pluginInterface.GetIpcSubscriber<JObject, bool>(name);
        var logger = container.Resolve<ILogger>();
        var dispatcher = container.Resolve<EventDispatcher>();
        var repository = container.Resolve<FFXIVRepository>();
        var framework = container.Resolve<IFramework>();
        return CreateHandler(name, receiver, sender, logger, dispatcher, repository, framework);
    }

    protected IHandler CreateHandler(
        string name, ICallGateProvider<JObject, bool> receiver, ICallGateSubscriber<JObject, bool> sender,
        ILogger logger, EventDispatcher eventDispatcher, FFXIVRepository repository, IFramework framework);
}

internal class HandlerFactory : IHandlerFactory
{
    public IHandler CreateHandler(
        string name, ICallGateProvider<JObject, bool> receiver, ICallGateSubscriber<JObject, bool> sender,
        ILogger logger, EventDispatcher eventDispatcher, FFXIVRepository repository, IFramework framework) =>
        new IpcHandler(name, receiver, sender, logger, eventDispatcher, framework);
}

internal class LegacyHandlerFactory : IHandlerFactory
{
    public IHandler CreateHandler(
        string name, ICallGateProvider<JObject, bool> receiver, ICallGateSubscriber<JObject, bool> sender,
        ILogger logger, EventDispatcher eventDispatcher, FFXIVRepository repository, IFramework framework) =>
        new LegacyIpcHandler(name, receiver, sender, logger, eventDispatcher, repository, framework);
}
