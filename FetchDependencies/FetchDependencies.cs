using System.IO.Compression;
using System.Text.Json.Nodes;

namespace FetchDependencies;

public class FetchDependencies
{
    private const string VersionUrlGlobal = "https://www.iinact.com/updater/version";
    private const string VersionUrlChinese = "https://cninact.diemoe.net/CN解析/版本.txt";
    private const string PluginUrlGlobal = "https://www.iinact.com/updater/download";
    private const string PluginUrlChinese = "https://cninact.diemoe.net/CN解析/FFXIV_ACT_Plugin.dll";

    // Minimum compatible version: 3.x moved CombatantStruct to Models.Global namespace
    private static readonly Version MinimumVersion = new Version(3, 0, 0, 0);

    private Version PluginVersion { get; }
    private string DependenciesDir { get; }
    private bool IsChinese { get; }
    private HttpClient HttpClient { get; }

    /// <summary>
    /// 解析依賴（FFXIV_ACT_Plugin）換版時的通報管道。
    /// 這個專案不參照 Dalamud、拿不到 IPluginLog，所以由呼叫端把 Information 級的寫入器傳進來；
    /// 不傳＝維持原本完全不出聲的行為。
    /// </summary>
    private Action<string>? LogInfo { get; }

    public FetchDependencies(Version version, string assemblyDir, bool isChinese, HttpClient httpClient,
                             Action<string>? logInfo = null)
    {
        PluginVersion = version;
        DependenciesDir = assemblyDir;
        IsChinese = isChinese;
        HttpClient = httpClient;
        LogInfo = logInfo;
    }

    public void GetFfxivPlugin()
    {
        var pluginZipPath = Path.Combine(DependenciesDir, "FFXIV_ACT_Plugin.zip");
        var pluginPath = Path.Combine(DependenciesDir, "FFXIV_ACT_Plugin.dll");

        // 換版可見性：更新前那一版的版本號由 NeedsUpdate 順手帶出來（它本來就要開這份組件，
        // 不另外多讀一次）。下載／解壓縮一旦跑下去，舊版本號就永遠拿不回來了 ——
        // 解析行為出現變化時，「是不是剛換版」是第一個要排除的變因。
        if (!NeedsUpdate(pluginPath, out var previousVersion))
            return;

        var downloadSource = "既有的 FFXIV_ACT_Plugin.zip";
        if (!File.Exists(pluginZipPath))
        {
            downloadSource = DownloadPlugin(pluginZipPath);
        }

        try
        {
            ZipFile.ExtractToDirectory(pluginZipPath, DependenciesDir, true);
        }
        catch (InvalidDataException)
        {
            File.Delete(pluginZipPath);
            downloadSource = DownloadPlugin(pluginZipPath);
            ZipFile.ExtractToDirectory(pluginZipPath, DependenciesDir, true);
        }
        File.Delete(pluginZipPath);

        // 在修補之前就先報告：修補萬一炸了，「依賴剛換過版」這個關鍵事實不能跟著一起消失。
        ReportDependencyVersionChange(previousVersion, TryReadDependencyVersion(pluginPath), downloadSource);

        foreach (var deucalionDll in Directory.GetFiles(DependenciesDir, "deucalion*.dll"))
            File.Delete(deucalionDll);

        var patcher = new Patcher(PluginVersion, DependenciesDir);
        patcher.MainPlugin();
        patcher.LogFilePlugin();
        patcher.MemoryPlugin();
    }

    /// <param name="currentVersion">
    /// 目前磁碟上那份的組件版本；讀不到（未安裝／損毀）時為 null。
    /// ⚠️ 這是純輸出，不參與「要不要更新」的判斷 —— 更新條件一個字都沒動。
    /// </param>
    private bool NeedsUpdate(string dllPath, out Version? currentVersion)
    {
        currentVersion = null;
        if (!File.Exists(dllPath)) return true;
        try
        {
            using var plugin = new TargetAssembly(dllPath);
            currentVersion = plugin.Version;

            if (!plugin.ApiVersionMatches())
                return true;

            // Force update if below minimum compatible version regardless of network status
            if (plugin.Version < MinimumVersion)
                return true;
            
            using var cancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var remoteVersionString = HttpClient
                                      .GetStringAsync(IsChinese ? VersionUrlChinese : VersionUrlGlobal,
                                                      cancelAfterDelay.Token).Result;
            var remoteVersion = new Version(remoteVersionString);
            return remoteVersion > plugin.Version;
        }
        catch
        {
            return false;
        }
    }

    /// <returns>實際下載成功的來源 URL（給換版通報用）。</returns>
    private string DownloadPlugin(string pluginZipPath)
    {
        var primaryUrl = IsChinese ? PluginUrlChinese : PluginUrlGlobal;
        try
        {
            DownloadFile(primaryUrl, pluginZipPath);
            return primaryUrl;
        }
        catch
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ravahn/FFXIV_ACT_Plugin/releases/latest");
            request.Headers.UserAgent.ParseAdd("IINACT/1.0");
            using var response = HttpClient.Send(request);
            response.EnsureSuccessStatusCode();

            using var stream = response.Content.ReadAsStream();
            var json = JsonNode.Parse(stream);
            var downloadUrl = json?["assets"]?[0]?["browser_download_url"]?.ToString();

            if (string.IsNullOrEmpty(downloadUrl))
                throw new Exception("Could not find fallback download URL from GitHub API.");

            DownloadFile(downloadUrl, pluginZipPath);
            return downloadUrl;
        }
    }

    /// <summary>
    /// 讀磁碟上那份解析 DLL 的組件版本。讀不到（檔案不存在／損毀／被鎖住）一律回 null ——
    /// 絕不為了「看一眼版本」而讓外掛載入失敗。
    /// </summary>
    private static Version? TryReadDependencyVersion(string dllPath)
    {
        if (!File.Exists(dllPath)) return null;
        try
        {
            using var plugin = new TargetAssembly(dllPath);
            return plugin.Version;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析依賴換版時寫一行 Information。
    /// <para>
    /// 這條路徑原本是完全靜默的：CDN 一推新版，下次進遊戲就換掉了，log 裡不留任何痕跡，
    /// 事後要判斷「解析結果變了是不是因為依賴換版」只能用猜的。
    /// </para>
    /// <para>
    /// ⚠️ 這裡只做「可見」，刻意不做版本閘門 —— 自動更新本身是有益的
    /// （3.0.2.5→3.0.2.7 實測解析品質不降反升），擋掉它才是行為回退。
    /// </para>
    /// </summary>
    private void ReportDependencyVersionChange(Version? previous, Version? current, string source)
    {
        var log = LogInfo;
        if (log == null) return;

        try
        {
            if (current == null)
                log("[FetchDependencies] 已重新安裝解析依賴 FFXIV_ACT_Plugin（來源：" + source + "），" +
                    "但安裝後讀不到版本號；安裝前是 " + (previous?.ToString() ?? "（未安裝）") + "。");
            else if (previous == null)
                log("[FetchDependencies] 首次安裝解析依賴 FFXIV_ACT_Plugin " + current +
                    "（來源：" + source + "）。");
            else if (previous != current)
                log("[FetchDependencies] 解析依賴 FFXIV_ACT_Plugin 已換版：" + previous + " → " + current +
                    "（來源：" + source + "）。這是上游 CDN 的自動更新，不是本外掛改的；" +
                    "解析結果若在這之後變了樣，第一個要排除的變因就是它。");
            else
                log("[FetchDependencies] 重新安裝解析依賴 FFXIV_ACT_Plugin " + current +
                    "（來源：" + source + "），版本沒變" +
                    "（多半是 IINACT API 標記或修補痕跡對不上，需要重新套用修補）。");
        }
        catch
        {
            // 診斷本身絕不能讓外掛載入失敗
        }
    }

    private void DownloadFile(string url, string path)
    {
        using var cancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var downloadStream = HttpClient
                                   .GetStreamAsync(url,
                                                   cancelAfterDelay.Token).Result;
        using var zipFileStream = new FileStream(path, FileMode.Create);
        downloadStream.CopyTo(zipFileStream);
        zipFileStream.Close();
    }
}
