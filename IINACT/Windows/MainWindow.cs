using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using FFXIV_ACT_Plugin.Config;
using RainbowMage.OverlayPlugin;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace IINACT.Windows;

public class MainWindow : Window, IDisposable
{
    private Plugin Plugin { get; }

    private int selectedOverlayIndex;

    public MainWindow(Plugin plugin) : base($"IINACT v{plugin.Version}")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(307, 207),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        Plugin = plugin;
    }

    public IPluginConfig? OverlayPluginConfig { get; set; }
    public IReadOnlyList<RainbowMage.OverlayPlugin.IOverlayTemplate>? OverlayPresets { get; set; }
    private string[]? OverlayNames => OverlayPresets?.Select(x => x.Name).ToArray();
    public RainbowMage.OverlayPlugin.WebSocket.ServerController? Server { get; set; }

    public void Dispose() { }

    public override void Draw()
    {
        using var bar = ImRaii.TabBar("settingsTabs");
        if (!bar) return;

        DrawMainWindow();
        DrawParseSettings();
        DrawWebSocketSettings();
    }

    private void DrawMainWindow()
    {
        // ###id keeps the ImGui tab identity stable across languages, so the tab bar's
        // remembered selection survives translation.
        using var tab = ImRaii.TabItem("Status".Loc() + "###Status");
        if (!tab) return;

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "OverlayPlugin Status:".Loc());
        ImGuiHelpers.ScaledRelativeSameLine(155);
        ImGui.Text(Plugin.OverlayPluginStatus);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudGrey, "Overlay URI generator:".Loc());

        var comboWidth = ImGui.GetWindowWidth() * 0.8f;
        
        var selectedIndexOverlayName = OverlayNames?[selectedOverlayIndex] ?? "";
        var selectedOverlayName = Plugin.Configuration.SelectedOverlay ?? selectedIndexOverlayName;
        if (selectedOverlayName != selectedIndexOverlayName)
            for (var i = 0; i < OverlayNames?.Length; i++)
                if (OverlayNames?[i] == selectedOverlayName) 
                    selectedOverlayIndex = i;
        
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("Overlay".Loc() + "###Overlay", selectedOverlayName))
        {
            for (var i = 0; i < OverlayNames?.Length; i++)
            {
                var currentOverlayName = OverlayNames?[i] ?? "";
                if (ImGui.Selectable(currentOverlayName, currentOverlayName == selectedOverlayName))
                {
                    selectedOverlayIndex = i;
                    Plugin.Configuration.SelectedOverlay = currentOverlayName;
                    Plugin.Configuration.Save();
                }
            }

            ImGui.EndCombo();
        }

        var selectedOverlay = OverlayPresets?[selectedOverlayIndex];
        Uri.TryCreate($"ws://{Server?.Address}:{Server?.Port}/ws", UriKind.Absolute, out var webSocketServer);
        var overlayUri = selectedOverlay?.ToOverlayUri(webSocketServer);
        var overlayUriString = overlayUri?.ToString() ?? "<Error generating URI>".Loc();

        // "URI" left untranslated on purpose: it labels the raw address the user copies out.
        ImGui.SetNextItemWidth(comboWidth);
        ImGui.InputText("URI", ref overlayUriString, 1000, ImGuiInputTextFlags.ReadOnly);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var serverStatus = Server is null ? "Initializing...".Loc() : "Stopped".Loc();

        if (Server?.Running ?? false)
            serverStatus = "Listening on ??:??".Loc(Server?.Address, Server?.Port);

        if (Server?.Failed ?? false)
        {
            // LastException.Message is a raw .NET/socket message - left as-is.
            serverStatus = Server.LastException?.Message ?? "Failed".Loc();
            if (Server.LastException is SocketException { ErrorCode: 10048 })
                serverStatus = "Port ?? is already in use".Loc(Server?.Port);
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "WebSocket Server:".Loc());
        ImGuiHelpers.ScaledRelativeSameLine(155);
        ImGui.Text(serverStatus);
        ImGui.GetWindowDpiScale();

        if (Server?.Running ?? false)
        {
            if (ImGui.Button("Stop".Loc() + "###Stop"))
                Server.Stop();

            ImGui.SameLine();

            if (ImGui.Button("Restart".Loc() + "###Restart"))
                Server.Restart();
        }
        else if (Server is not null)
        {
            if (ImGui.Button("Start".Loc() + "###Start"))
                Server.Start();
        }
    }

     private void DrawParseSettings()
    {
        using var tab = ImRaii.TabItem("Parser".Loc() + "###Parser");
        if (!tab) return;

        ImGui.Spacing();
        var elementWidth = ImGui.GetWindowWidth() - (150 * ImGuiHelpers.GlobalScale);
        var logFilePath = Plugin.Configuration.LogFilePath;
        ImGui.SetNextItemWidth(elementWidth);
        ImGui.InputText("Log File Path".Loc() + "###Log File Path", ref logFilePath, 200, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiComponents.DisabledButton(FontAwesomeIcon.Folder))
        {
            Plugin.FileDialogManager.OpenFolderDialog("Pick a folder to save logs to".Loc(), (success, path) =>
            {
                if (!success) return;
                Plugin.Configuration.LogFilePath = path;
                Plugin.Configuration.Save();
            }, Plugin.Configuration.LogFilePath);
        }
        ImGui.Spacing();
        ImGui.SetNextItemWidth(elementWidth);
        // The combo's *items* are Enum.GetName values from FFXIV_ACT_Plugin.Config -
        // identifiers coming out of a third-party assembly, so they stay English.
        if (ImGui.BeginCombo("Parse Filter".Loc() + "###Parse Filter",
                             Enum.GetName(typeof(ParseFilterMode), Plugin.Configuration.ParseFilterMode)))
        {
            foreach (var filter in Enum.GetValues<ParseFilterMode>())
                if (ImGui.Selectable(Enum.GetName(typeof(ParseFilterMode), filter),
                                     (ParseFilterMode)Plugin.Configuration.ParseFilterMode == filter))
                {
                    Plugin.Configuration.ParseFilterMode = (int)filter;
                    Plugin.Configuration.Save();
                }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
        
        var writeLogFile = Plugin.Configuration.WriteLogFile;
        if (ImGui.Checkbox("Write out network log file".Loc() + "###Write out network log file", ref writeLogFile))
        {
            Plugin.Configuration.WriteLogFile = writeLogFile;
            Plugin.Configuration.Save();
        }

        var disablePvp = Plugin.Configuration.DisablePvp;
        if (ImGui.Checkbox("Disable writing out network log file in PvP".Loc() + "###Disable writing out network log file in PvP", ref disablePvp))
        {
            if (Plugin.ClientState.IsPvP && disablePvp) Plugin.Configuration.DisableWritingPvpLogFile = true;

            Plugin.Configuration.DisablePvp = disablePvp;
            Plugin.Configuration.Save();
        }

        var disableDamageShield = Plugin.Configuration.DisableDamageShield;
        if (ImGui.Checkbox("Disable Damage Shield Estimates".Loc() + "###Disable Damage Shield Estimates", ref disableDamageShield))
        {
            Plugin.Configuration.DisableDamageShield = disableDamageShield;
            Plugin.Configuration.Save();
        }

        var disableCombinePets = Plugin.Configuration.DisableCombinePets;
        if (ImGui.Checkbox("Disable Combine Pets with Owners".Loc() + "###Disable Combine Pets with Owners", ref disableCombinePets))
        {
            Plugin.Configuration.DisableCombinePets = disableCombinePets;
            Plugin.Configuration.Save();
        }

        var showDebug = Plugin.Configuration.ShowDebug;
        if (ImGui.Checkbox("Show Debug Options".Loc() + "###Show Debug Options", ref showDebug))
        {
            Plugin.Configuration.ShowDebug = showDebug;
            Plugin.Configuration.Save();
        }

        if (!showDebug) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var simulateIndividualDoTCrits = Plugin.Configuration.SimulateIndividualDoTCrits;
        if (ImGui.Checkbox("Simulate Individual DoT Crits".Loc() + "###Simulate Individual DoT Crits", ref simulateIndividualDoTCrits))
        {
            Plugin.Configuration.SimulateIndividualDoTCrits = simulateIndividualDoTCrits;
            Plugin.Configuration.Save();
        }

        var showRealDoTTicks = Plugin.Configuration.ShowRealDoTTicks;
        if (ImGui.Checkbox("Also Show 'Real' DoT Ticks".Loc() + "###Also Show 'Real' DoT Ticks", ref showRealDoTTicks))
        {
            Plugin.Configuration.ShowRealDoTTicks = showRealDoTTicks;
            Plugin.Configuration.Save();
        }
    }

    private void DrawWebSocketSettings()
    {
        using var tab = ImRaii.TabItem("WebSocket Server".Loc() + "###WebSocket Server");
        if (!tab) return;
        
        ImGui.Spacing();

        // 本幀開始前的設定值,用來判斷結尾是否真的需要存檔。
        // OverlayPluginConfig.Save() 會強制完整序列化(解析舊檔、產生 .bak、寫回整份設定),
        // 無條件每幀呼叫等於停在這個分頁就一直在主執行緒做完整檔案 I/O。
        var originalWsServerIp = OverlayPluginConfig?.WSServerIP;
        var originalWsServerPort = OverlayPluginConfig?.WSServerPort;

        // "IP" / "Port" left untranslated on purpose: they label raw connection
        // parameters the user types verbatim, and are read the same way in every locale.
        var wsServerIp = OverlayPluginConfig?.WSServerIP ?? "";
        ImGui.InputText("IP", ref wsServerIp, 100, ImGuiInputTextFlags.None);

        if (IPAddress.TryParse(wsServerIp, out var address))
        {
            if (OverlayPluginConfig is not null)
                OverlayPluginConfig.WSServerIP = address.ToString();
        }
        else if (wsServerIp == "*")
        {
            if (OverlayPluginConfig is not null)
                OverlayPluginConfig.WSServerIP = "*";
        }

        var wsServerPort = OverlayPluginConfig?.WSServerPort.ToString() ?? "";
        ImGui.InputText("Port", ref wsServerPort, 100, ImGuiInputTextFlags.None);

        if (int.TryParse(wsServerPort, out var port))
        {
            if (OverlayPluginConfig is not null)
                OverlayPluginConfig.WSServerPort = port;
        }

        if (OverlayPluginConfig is null) return;
        if (OverlayPluginConfig.WSServerIP == originalWsServerIp &&
            OverlayPluginConfig.WSServerPort == originalWsServerPort)
            return;

        OverlayPluginConfig.Save();
    }

}
