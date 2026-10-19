using System.IO.Compression;
using System.Security.Cryptography;
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

    private sealed record KnownPluginZip(string Version, string Sha256, string PinnedUrl);

    /// <summary>
    /// 只解壓這裡列出的 zip（整個 zip 檔的 SHA-256）。上游換版時同一版要各登記一筆：
    /// iinact.com 的 zip 只含主 DLL，GitHub release 的 zip 另帶 deucalion，兩者雜湊不同。
    /// <c>PinnedUrl</c> 是該版固定網址，本機沒有可用 DLL 而最新版又不在清單時改抓它。
    /// </summary>
    private static readonly KnownPluginZip[] KnownPluginZips =
    {
        new("3.0.3.3", "09B4767091ACFEF11BEC2660F73F5124C7815DEEBB6C3363C547040693D85FBA",
            "https://softwareupdate.xivmac.com/sites/default/files/update_data/IINACT/FFXIV_ACT_Plugin_3.0.3.3.zip"),
        new("3.0.3.3", "8254F003CB8018B7F6C66208400BD3191793D1C4A5CBD839E1331316A9351472",
            "https://github.com/ravahn/FFXIV_ACT_Plugin/releases/download/3.0.3.3/FFXIV_ACT_Plugin_3.0.3.3.zip"),
        new("3.0.3.1", "5851BFD9E621918C7E53BC815DAD0A8AEF0994834BB24ACC05AAC3E031EED3C1",
            "https://softwareupdate.xivmac.com/sites/default/files/update_data/IINACT/FFXIV_ACT_Plugin_3.0.3.1.zip"),
        new("3.0.3.1", "9703CCF9F2937FB4840C42B1B82E1D4834F5E81BA9EC80E3C234F54D251525F5",
            "https://github.com/ravahn/FFXIV_ACT_Plugin/releases/download/3.0.3.1/FFXIV_ACT_Plugin_3.0.3.1.zip"),
    };

    private Version PluginVersion { get; }
    private string DependenciesDir { get; }
    private bool IsChinese { get; }
    private HttpClient HttpClient { get; }

    /// <summary>
    /// 解析依賴（FFXIV_ACT_Plugin）換版時的通報管道。
    /// 這個專案不參照 Dalamud、拿不到 IPluginLog，所以由呼叫端把 Information 級的寫入器傳進來；
    /// 不傳＝不出聲。
    /// </summary>
    private Action<string>? LogInfo { get; }

    /// <summary>下載的 zip 被拒或下載失敗時的 Warning 級寫入器；不傳＝不出聲。</summary>
    private Action<string>? LogWarning { get; }

    /// <summary>給使用者的遊戲內通知；每次 <see cref="GetFfxivPlugin"/> 最多呼叫一次。</summary>
    private Action<string>? NotifyUser { get; }

    public FetchDependencies(Version version, string assemblyDir, bool isChinese, HttpClient httpClient,
                             Action<string>? logInfo = null, Action<string>? logWarning = null,
                             Action<string>? notifyUser = null)
    {
        PluginVersion = version;
        DependenciesDir = assemblyDir;
        IsChinese = isChinese;
        HttpClient = httpClient;
        LogInfo = logInfo;
        LogWarning = logWarning;
        NotifyUser = notifyUser;
    }

    public void GetFfxivPlugin()
    {
        var pluginZipPath = Path.Combine(DependenciesDir, "FFXIV_ACT_Plugin.zip");
        var pluginPath = Path.Combine(DependenciesDir, "FFXIV_ACT_Plugin.dll");

        // 更新前那一版的版本號由 NeedsUpdate 順手帶出來（它本來就要開這份組件）：
        // 下載／解壓縮一旦跑下去，舊版本號就拿不回來了。
        if (!NeedsUpdate(pluginPath, out var previousVersion, out var previousUsable))
            return;

        var attempts = new FetchAttempts();
        var downloadSource = AcquireVerifiedZip(pluginZipPath, previousUsable, attempts);
        if (downloadSource == null)
        {
            // 走到這裡時 zip 已刪、目錄裡的既有檔案一個都沒動過。
            HandleNoVerifiedZip(attempts, previousUsable ? previousVersion : null);
            return;
        }

        // 損毀的 zip 過不了上面的雜湊檢查，所以這裡不再有「解壓失敗就重新下載」。
        ZipFile.ExtractToDirectory(pluginZipPath, DependenciesDir, true);
        File.Delete(pluginZipPath);

        if (attempts.UsedPinnedFallback)
        {
            var message = (attempts.RejectedByHash
                               ? "最新的解析元件 FFXIV_ACT_Plugin 不在已知版本清單"
                               : "下載最新的解析元件 FFXIV_ACT_Plugin 失敗") +
                          "，已改裝已知版本（來源：" + downloadSource + "）。";
            SafeInvoke(LogWarning, "[FetchDependencies] " + message + " " + attempts.Describe() + " " +
                                   DescribeKnownList());
            SafeInvoke(NotifyUser, message);
        }
        else if (attempts.Lines.Count > 0)
        {
            SafeInvoke(LogWarning, "[FetchDependencies] 已安裝來源 " + downloadSource + "。" + attempts.Describe());
        }

        // 在修補之前就先報告：修補萬一炸了，「依賴剛換過版」這個關鍵事實不能跟著一起消失。
        ReportDependencyVersionChange(previousVersion, TryReadDependencyVersion(pluginPath), downloadSource);

        foreach (var deucalionDll in Directory.GetFiles(DependenciesDir, "deucalion*.dll"))
            File.Delete(deucalionDll);

        var patcher = new Patcher(PluginVersion, DependenciesDir, LogInfo, LogWarning);
        patcher.MainPlugin();
        patcher.LogFilePlugin();
        patcher.MemoryPlugin();
        patcher.ResourcePlugin();
    }

    /// <param name="currentVersion">
    /// 目前磁碟上那份的組件版本；讀不到（未安裝／損毀）時為 null。
    /// 純輸出，不參與「要不要更新」的判斷。
    /// </param>
    /// <param name="currentUsable">磁碟上那份已修補、版本也夠新，換不了新版時可以直接沿用。</param>
    private bool NeedsUpdate(string dllPath, out Version? currentVersion, out bool currentUsable)
    {
        currentVersion = null;
        currentUsable = false;
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

            currentUsable = true;
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

    private sealed class FetchAttempts
    {
        public List<string> Lines { get; } = new();
        public bool RejectedByHash { get; set; }
        public bool UsedPinnedFallback { get; set; }

        public string Describe() => "嘗試過的來源：" + string.Join("；", Lines) + "。";
    }

    /// <summary>
    /// 依序取得一份雜湊在 <see cref="KnownPluginZips"/> 裡的 zip 放到 <paramref name="zipPath"/>。
    /// 不擲例外；回 null 時 <paramref name="zipPath"/> 保證不存在。
    /// </summary>
    /// <param name="currentUsable">為真時不改抓固定網址的舊版（沿用現有 DLL 即可）。</param>
    private string? AcquireVerifiedZip(string zipPath, bool currentUsable, FetchAttempts attempts)
    {
        const string leftoverSource = "既有的 FFXIV_ACT_Plugin.zip";
        if (File.Exists(zipPath) && VerifyZip(zipPath, leftoverSource, attempts))
            return leftoverSource;

        string? source = null;
        try
        {
            source = DownloadPlugin(zipPath);
        }
        catch (Exception e)
        {
            TryDelete(zipPath);
            attempts.Lines.Add((IsChinese ? PluginUrlChinese : PluginUrlGlobal) + " 與 GitHub 備援皆下載失敗（" +
                               e.GetBaseException().Message + "）");
        }

        if (source != null && VerifyZip(zipPath, source, attempts))
            return source;

        if (currentUsable)
            return null;

        foreach (var known in KnownPluginZips.OrderByDescending(k => new Version(k.Version)))
        {
            try
            {
                DownloadFile(known.PinnedUrl, zipPath);
            }
            catch (Exception e)
            {
                TryDelete(zipPath);
                attempts.Lines.Add(known.PinnedUrl + " 下載失敗（" + e.GetBaseException().Message + "）");
                continue;
            }

            if (VerifyZip(zipPath, known.PinnedUrl, attempts))
            {
                attempts.UsedPinnedFallback = true;
                return known.PinnedUrl;
            }
        }

        return null;
    }

    /// <summary>雜湊不在清單（或讀不了）就刪掉 zip 並記一筆，回 false。</summary>
    private static bool VerifyZip(string zipPath, string source, FetchAttempts attempts)
    {
        string hash;
        try
        {
            using var stream = File.OpenRead(zipPath);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception e)
        {
            TryDelete(zipPath);
            attempts.Lines.Add(source + " 無法計算雜湊（" + e.GetBaseException().Message + "）");
            return false;
        }

        if (KnownPluginZips.Any(k => string.Equals(k.Sha256, hash, StringComparison.OrdinalIgnoreCase)))
            return true;

        TryDelete(zipPath);
        attempts.RejectedByHash = true;
        attempts.Lines.Add(source + " 的 SHA-256 為 " + hash + "，不在已知清單");
        return false;
    }

    /// <summary>
    /// 沒有可安裝的 zip。有可沿用的 DLL（<paramref name="usableVersion"/> 非 null）就照舊載入；
    /// 沒有的話擲出說明清楚的例外 —— 缺了這個 DLL 插件本來就載不起來，與其在後面報找不到組件，不如在這裡講明原因。
    /// </summary>
    private void HandleNoVerifiedZip(FetchAttempts attempts, Version? usableVersion)
    {
        var reason = attempts.RejectedByHash
                         ? "下載的解析元件 FFXIV_ACT_Plugin 不在已知版本清單，為了安全沒有安裝"
                         : "下載解析元件 FFXIV_ACT_Plugin 失敗";
        var detail = attempts.Describe() + " " + DescribeKnownList();

        if (usableVersion != null)
        {
            var message = reason + "，繼續使用目前的 " + usableVersion + "。";
            SafeInvoke(LogWarning, "[FetchDependencies] " + message + " " + detail);
            SafeInvoke(NotifyUser, message);
            return;
        }

        var fatal = "IINACT 無法載入：" + reason + "，本機也沒有可用的舊版。" +
                    (attempts.RejectedByHash ? "請等 IINACT 插件更新。" : "請確認網路後重新啟用插件。");
        SafeInvoke(LogWarning, "[FetchDependencies] " + fatal + " " + detail);
        SafeInvoke(NotifyUser, fatal);
        throw new InvalidOperationException(fatal);
    }

    private static string DescribeKnownList() =>
        "已知清單：" + string.Join("、", KnownPluginZips.Select(k => k.Version + " " + k.Sha256)) + "。";

    private static void SafeInvoke(Action<string>? sink, string message)
    {
        if (sink == null) return;
        try
        {
            sink(message);
        }
        catch
        {
            // 通報失敗不能改變載入結果
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 留下的 zip 下次載入會先過雜湊檢查，不會被直接解壓
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
    /// 這裡只做「可見」；要不要裝由 <see cref="AcquireVerifiedZip"/> 的雜湊清單決定。
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
                    "（來源：" + source + "）。這是上游 CDN 的自動更新，不是本插件改的；" +
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
