using System.Diagnostics;
using System.Reflection;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using IINACT.Network;
using IINACT.Windows;
using Machina.FFXIV;
using Machina.FFXIV.Headers.Opcodes;

namespace IINACT;

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "IINACT";
    public Version Version { get; }

    private const string MainWindowCommandName = "/iinact";
    private const string EndEncCommandName = "/endenc";
    public readonly WindowSystem WindowSystem = new("IINACT");
    
    internal IDalamudPluginInterface PluginInterface { get; }
    internal ICommandManager CommandManager { get; }
    internal IClientState ClientState { get; }
    internal IDataManager DataManager { get; }
    internal IChatGui ChatGui { get; }
    internal IFramework Framework { get; }
    internal ICondition Condition { get; }
    internal IGameInteropProvider GameInteropProvider { get; }
    internal ISigScanner SigScanner { get; }
    internal INotificationManager NotificationManager { get; }
    public static IPluginLog Log { get; private set; } = null!;

    internal Configuration Configuration { get; }
    private TextToSpeechProvider TextToSpeechProvider { get; }
    private MainWindow MainWindow { get; }
    internal FileDialogManager FileDialogManager { get; }
    private ZoneDownHookManager? ZoneDownHookManager { get; }
    private IpcProviders IpcProviders { get; }

    private FfxivActPluginWrapper FfxivActPluginWrapper { get; }
    internal ParserWatchdog ParserWatchdog { get; }
    private RainbowMage.OverlayPlugin.PluginMain OverlayPlugin { get; set; }
    private RainbowMage.OverlayPlugin.WebSocket.ServerController? WebSocketServer { get; set; }
    internal string OverlayPluginStatus => OverlayPlugin.Status;
    private PluginLogTraceListener PluginLogTraceListener { get; }
    private HttpClient HttpClient { get; }
    private RainbowMage.OverlayPlugin.ChatEcho ChatEcho { get; }

    public Plugin(IDalamudPluginInterface pluginInterface,
                  ICommandManager commandManager,
                  IClientState clientState,
                  IDataManager dataManager,
                  IChatGui chatGui,
                  IFramework framework,
                  ICondition condition,
                  IPluginLog pluginLog,
                  IGameInteropProvider gameInteropProvider,
                  ISigScanner sigScanner,
                  INotificationManager notificationManager,
                  IObjectTable objectTable)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        DataManager = dataManager;
        ClientState = clientState;
        ChatGui = chatGui;
        Framework = framework;
        Condition = condition;
        GameInteropProvider = gameInteropProvider;
        SigScanner = sigScanner;
        NotificationManager = notificationManager;
        Log = pluginLog;
        ChatEcho = new RainbowMage.OverlayPlugin.ChatEcho(chatGui, framework, clientState, objectTable);

        Localization.Init(PluginInterface.AssemblyLocation.Directory?.FullName);

        OpcodeManager.Instance.SetRegion(GameRegion.TraditionalChinese);

        var createZoneDownHookManager = Task.Run(() 
            => new ZoneDownHookManager(NotificationManager, GameInteropProvider));
        Version = Assembly.GetExecutingAssembly().GetName().Version!;

        FileDialogManager = new FileDialogManager();

        HttpClient = new HttpClient();
        
        var fetchDeps =
            new FetchDependencies.FetchDependencies(Version, PluginInterface.AssemblyLocation.Directory!.FullName,
                                                    false, HttpClient, // TC branch always uses global CDN (iinact.com)
                                                    // 解析依賴換版要看得見：要使用者回報的診斷。
                                                    message => Log.Information(message),
                                                    message => Log.Warning(message),
                                                    message => NotificationManager.AddNotification(new Notification
                                                    {
                                                        Content = message,
                                                        Title = "IINACT",
                                                        Type = NotificationType.Warning,
                                                    }));
        
        fetchDeps.GetFfxivPlugin();
        
        PluginLogTraceListener = new PluginLogTraceListener();
        Trace.Listeners.Add(PluginLogTraceListener);

        Advanced_Combat_Tracker.ActGlobals.Init();
        Advanced_Combat_Tracker.ActGlobals.oFormActMain = new Advanced_Combat_Tracker.FormActMain(Log);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        this.TextToSpeechProvider = new TextToSpeechProvider(Configuration);
        Advanced_Combat_Tracker.ActGlobals.oFormActMain.LogFilePath = Configuration.LogFilePath;

        FfxivActPluginWrapper = new FfxivActPluginWrapper(Configuration, DataManager.Language, ChatGui, Framework, Condition);
        ParserWatchdog = new ParserWatchdog(FfxivActPluginWrapper, () => ZoneDownHookManager, Condition, ClientState, ChatGui, Configuration);
        Task.Run(() => NetworkLogCleanup.Cleanup(Configuration));
        OverlayPlugin = InitOverlayPlugin();

        IpcProviders = new IpcProviders(PluginInterface);

        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(MainWindowCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Displays the IINACT main window".Loc()
        });

        CommandManager.AddHandler(EndEncCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Ends the current encounter IINACT is parsing".Loc()
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;
        
        if (clientState.IsPvP)
            EnterPvP();
        else
            LeavePvP();
        
        ClientState.EnterPvP += EnterPvP;
        ClientState.LeavePvP += LeavePvP;

        ZoneDownHookManager = createZoneDownHookManager.Result;
        Framework.Update += ParserWatchdog.Tick;
    }

    public void Dispose()
    {
        Framework.Update -= ParserWatchdog.Tick;
        ClientState.EnterPvP -= EnterPvP;
        ClientState.LeavePvP -= LeavePvP;
        IpcProviders.Dispose();
        ChatEcho.Dispose();
        ZoneDownHookManager.Dispose();
        
        FfxivActPluginWrapper.Dispose();
        if (!FfxivActPluginWrapper.StoppedCleanly)
        {
            DisposeFrameworkSideOnly();
            return;
        }

        OverlayPlugin.DeInitPlugin();
        TextToSpeechProvider.Dispose();
        Trace.Listeners.Remove(PluginLogTraceListener);

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();

        CommandManager.RemoveHandler(MainWindowCommandName);
        CommandManager.RemoveHandler(EndEncCommandName);

        Advanced_Combat_Tracker.ActGlobals.Dispose();
    }

    // FFXIV_ACT_Plugin 沒停下：它的執行緒與它觸發的事件處理器碰得到的（OverlayPlugin、NotACT 與全域、
    // 語音、Trace 監聽）全部留著，否則它們之後撞到 null 或已關閉的物件會在自己的執行緒上擲未處理例外＝遊戲結束。
    // 只拆框架執行緒上、那些執行緒碰不到的 Dalamud 端東西。
    private void DisposeFrameworkSideOnly()
    {
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        CommandManager.RemoveHandler(MainWindowCommandName);
        CommandManager.RemoveHandler(EndEncCommandName);

        Log.Warning(
            "[卸載] IINACT 這次沒能乾淨卸載：解析元件 FFXIV_ACT_Plugin 沒有在時限內停下，它的執行緒可能還在跑。" +
            "為了不讓那些執行緒碰到已清掉的東西而讓遊戲結束，以下刻意留著不拆：" +
            "OverlayPlugin（含 WebSocket 伺服器，連接埠仍被佔用、疊加層可能停在舊資料）、" +
            "ACT 相容層（FormActMain 與它的讀行／寫檔／戰鬥動作執行緒、ActGlobals 全域）、" +
            "FFXIV_ACT_Plugin 本身與它的事件訂閱、共用記憶體區塊、文字轉語音、Trace 監聽。" +
            "這份 IINACT 會一直留在記憶體裡；方便時請重開遊戲（這之前重新載入 IINACT，疊加層的連接埠可能被舊的佔住）。");
    }

    private RainbowMage.OverlayPlugin.PluginMain InitOverlayPlugin()
    {
        var container = new RainbowMage.OverlayPlugin.TinyIoCContainer();
        
        var logger = new RainbowMage.OverlayPlugin.Logger(Log);
        container.Register(logger);
        container.Register<RainbowMage.OverlayPlugin.ILogger>(logger);

        container.Register(HttpClient);
        container.Register(FileDialogManager);
        container.Register(PluginInterface);
        // 戰鬥對象記憶體用它從遊戲碼判斷版面世代（CharacterLayoutFingerprint）。
        container.Register<ISigScanner>(SigScanner);
        container.Register(ChatEcho);

        var overlayPlugin = new RainbowMage.OverlayPlugin.PluginMain(
            PluginInterface.AssemblyLocation.Directory!.FullName, logger, container);
        container.Register(overlayPlugin);
        Advanced_Combat_Tracker.ActGlobals.oFormActMain.OverlayPluginContainer = container;
        
        Task.Run(() =>
        {
            overlayPlugin.InitPlugin(PluginInterface.ConfigDirectory.FullName);

            var registry = container.Resolve<RainbowMage.OverlayPlugin.Registry>();
            MainWindow.OverlayPresets = registry.OverlayTemplates;
            WebSocketServer = container.Resolve<RainbowMage.OverlayPlugin.WebSocket.ServerController>();
            MainWindow.Server = WebSocketServer;
            IpcProviders.Server = WebSocketServer;
            IpcProviders.OverlayIpcHandler = container.Resolve<RainbowMage.OverlayPlugin.Handlers.Ipc.IpcHandlerController>();
            MainWindow.OverlayPluginConfig = container.Resolve<RainbowMage.OverlayPlugin.IPluginConfig>();
            MainWindow.OverlayPluginEventConfig = container.Resolve<RainbowMage.OverlayPlugin.EventSources.BuiltinEventConfig>();
        });

        return overlayPlugin;
    }

    private void OnCommand(string command, string args)
    {
        if (command == EndEncCommandName)
        {
            Advanced_Combat_Tracker.ActGlobals.oFormActMain.EndCombat(false);
            return;
        }
            
        switch (args) 
        {
            case "start": //deprecated
            case "ws start":
                WebSocketServer?.Start();
                break;
            case "stop": //deprecated
            case "ws stop":
                WebSocketServer?.Stop();
                break;
            case "log start":
                Configuration.WriteLogFile = true;
                Configuration.Save();
                break;
            case "log stop":
                Configuration.WriteLogFile = false;
                Configuration.Save();
                break;
            case "log pvp start":
                Configuration.DisablePvp = false;
                Configuration.Save();
                break;
            case "log pvp stop":
                Configuration.DisablePvp = true;
                Configuration.Save();
                break;
            default:
                MainWindow.IsOpen = true;
                break;
        }
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
        FileDialogManager.Draw();
    }

    public void DrawConfigUI()
    {
        MainWindow.IsOpen = true;
    }

    internal void SetChatMessageLoggingEnabled(bool enabled)
    {
        FfxivActPluginWrapper.SetChatMessageLoggingEnabled(enabled);
    }

    private void EnterPvP()
    {
        if (Configuration is not { DisablePvp: true, DisableWritingPvpLogFile: false })
            return;

        Configuration.DisableWritingPvpLogFile = true;
    }

    private void LeavePvP()
    {
        Configuration.DisableWritingPvpLogFile = false;
    }
}
