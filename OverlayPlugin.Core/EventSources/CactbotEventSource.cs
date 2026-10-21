using Advanced_Combat_Tracker;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin.MemoryProcessors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Dalamud.Interface.ImGuiFileDialog;

namespace RainbowMage.OverlayPlugin.EventSources;

public class CactbotEventSource : EventSourceBase
{
    public CactbotEventSourceConfig Config { get; private set; }

    private const int KFastTimerMilli = 16;
    private const int KSlowTimerMilli = 300;
    private const int KUberSlowTimerMilli = 3000;

    private readonly SemaphoreSlim logLinesSemaphore = new(1);

    // Not thread-safe, as OnLogLineRead may happen at any time. Use |log_lines_semaphore_| to access it.
    private List<string> logLines = new(40);

    private List<string> importLogLines = new(40);

    // Used on the fast timer to avoid allocating List every time.
    private List<string> lastLogLines = new(40);
    private List<string> lastImportLogLines = new(40);

    // When true, the update function should reset notify state back to defaults.
    private bool resetNotifyState;

    // 只在快速計時器上讀寫。真＝玩家狀態有變化沒送出（沒人訂閱），快取裡沒有現值。
    private bool playerEventStale = true;

    private System.Timers.Timer fastUpdateTimer;

    // Held while the |fast_update_timer_| is running.
    private FFXIVProcess ffxiv;

    private string language;
    private string pcLocale;
    private List<FileSystemWatcher> watchers;
    private static int fileWatcherFailureLogged;

    public const string ForceReloadEvent = "onForceReload";
    public const string GameExistsEvent = "onGameExistsEvent";
    public const string GameActiveChangedEvent = "onGameActiveChangedEvent";
    public const string LogEvent = "onLogEvent";
    public const string ImportLogEvent = "onImportLogEvent";
    public const string InCombatChangedEvent = "onInCombatChangedEvent";
    public const string ZoneChangedEvent = "onZoneChangedEvent";
    public const string PlayerDiedEvent = "onPlayerDied";
    public const string PartyWipeEvent = "onPartyWipe";
    public const string PlayerChangedEvent = "onPlayerChangedEvent";
    public const string SendSaveDataEvent = "onSendSaveData";
    public const string DataFilesReadEvent = "onDataFilesRead";
    public const string InitializeOverlayEvent = "onInitializeOverlay";

    public void Wipe()
    {
        DispatchToJs(new JSEvents.PartyWipeEvent());
    }

    public CactbotEventSource(TinyIoCContainer container)
        : base(container)
    {
        Name = "Cactbot";

        RegisterEventTypes(new List<string>()
        {
            "onForceReload",
            "onLogEvent",
            "onImportLogEvent",
            "onPlayerDied",
            "onPartyWipe",
            "onUserFileChanged",
        });
        // cactbot 只在頁面載入時要一次狀態（cactbotRequestState），晚連上的頁面要在訂閱當下拿到最後的值。
        RegisterCachedEventTypes(new List<string>()
        {
            GameExistsEvent,
            GameActiveChangedEvent,
            InCombatChangedEvent,
            ZoneChangedEvent,
            PlayerChangedEvent,
        });

        // Broadcast onConfigChanged when a cactbotNotifyConfigChanged message occurs.
        RegisterEventHandler("cactbotReloadOverlays", (_) =>
        {
            DispatchToJs(new JSEvents.ForceReloadEvent());
            return null;
        });
        RegisterEventHandler("cactbotLoadUser", FetchUserFiles);
        RegisterEventHandler("cactbotReadDataFiles", FetchDataFiles);
        RegisterEventHandler("cactbotRequestPlayerUpdate", (_) =>
        {
            notifyState.Player = null;
            return null;
        });
        RegisterEventHandler("cactbotRequestState", (_) =>
        {
            resetNotifyState = true;
            return null;
        });
        RegisterEventHandler("cactbotSay", (msg) =>
        {
            ActGlobals.oFormActMain.TTS(msg["text"].ToString());
            return null;
        });
        RegisterEventHandler("cactbotSaveData", (msg) =>
        {
            Config.OverlayData[msg["overlay"].ToString()] = msg["data"];
            Config.OnUpdateConfig();
            return null;
        });
        RegisterEventHandler("cactbotLoadData", (msg) =>
        {
            if (Config.OverlayData.ContainsKey(msg["overlay"].ToString()))
            {
                var ret = new JObject
                {
                    ["data"] = Config.OverlayData[msg["overlay"].ToString()]
                };
                return ret;
            }
            else
            {
                return null;
            }
        });
        RegisterEventHandler("cactbotChooseDirectory", (_) =>
        {
            var ret = new JObject();
            var data = ChooseDirectory();
            if (data != null)
                ret["data"] = data;
            return ret;
        });
    }

    private void Log(LogLevel level, string msg)
    {
        logger.Log(level, "Cactbot: " + msg);
    }

    private string ChooseDirectory()
    {
        var fileDialogManager = container.Resolve<FileDialogManager>();
        var semaphore = new SemaphoreSlim(0);
        string chosenFolder = null;
        fileDialogManager.OpenFolderDialog("Pick a cactbot user folder", (success, path) =>
        {
            if (!success)
            {
                semaphore.Release();
                return;
            }

            chosenFolder = path;
            semaphore.Release();
        });
        semaphore.Wait();
        return chosenFolder;
    }

    public override void LoadConfig(IPluginConfig config)
    {
        Config = CactbotEventSourceConfig.LoadConfig(config, logger);
        Config.OverlayData ??= new Dictionary<string, JToken>();
    }

    public override void SaveConfig(IPluginConfig config)
    {
        Config.SaveConfig(config);
    }

    public override void Start()
    {
        var ffxivRepository = container.Resolve<FFXIVRepository>();
        if (!ffxivRepository.IsFFXIVPluginPresent())
        {
            Log(LogLevel.Error, "FFXIV plugin not found. Not initializing.");
            return;
        }

        // Our own timer with a higher frequency than OverlayPlugin since we want to see
        // the effect of log messages quickly.
        // TODO: Cleanup; Log messages are distributed through events and skip the update
        //   loop. Memory scanning needs a high frequency but that's handled by the
        //   MemoryProcessor classes which raise events.
        //   Everything else should be handled through events to avoid unnecessary polling.
        //   -- ngld
        fastUpdateTimer = new System.Timers.Timer();
        fastUpdateTimer.Elapsed += (_, _) =>
        {
            var timerInterval = KSlowTimerMilli;
            try
            {
                timerInterval = SendFastRateEvents();
            }
            catch (Exception e)
            {
                // SendFastRateEvents holds this semaphore until it exits.
                LogError("Exception in SendFastRateEvents: " + e.Message);
                LogError("Stack: " + e.StackTrace);
                LogError("Source: " + e.Source);
            }

            fastUpdateTimer.Interval = timerInterval;
        };
        fastUpdateTimer.AutoReset = false;

        language = ffxivRepository.GetLocaleString();
        pcLocale = System.Globalization.CultureInfo.CurrentUICulture.Name;
        
        var actVersion = typeof(ActGlobals).Assembly.GetName().Version!;

        // Print out version strings and locations to help users debug.
        LogInfo("OverlayPlugin Version: {0}", ffxivRepository.GetOverlayPluginVersion().ToString());
        LogInfo("FFXIV Plugin Version: {0}", ffxivRepository.GetPluginVersion().ToString());
        LogInfo("ACT Version: {0}", actVersion.ToString());

        LogInfo("Parsing Plugin Language: {0}", language ?? "(unknown)");

        LogInfo("System Locale: {0}", pcLocale ?? "(unknown)");

        switch (language)
        {
            case "cn":
                this.ffxiv = new FFXIVProcessCn(container);
                LogInfo("Version: cn");
                break;
            case "ko":
                this.ffxiv = new FFXIVProcessKo(container);
                LogInfo("Version: ko");
                break;
            default:
                this.ffxiv = new FFXIVProcessIntl(container);
                LogInfo("Version: intl");
                break;
        }

        // Incoming events.
        ActGlobals.oFormActMain.OnLogLineRead += OnLogLineRead;

        fastUpdateTimer.Interval = KFastTimerMilli;
        fastUpdateTimer.Start();

        // Start watching files after the update check.
        Config.WatchFileChangesChanged += (_, _) =>
        {
            if (Config.WatchFileChanges)
            {
                StartFileWatcher();
            }
            else
            {
                StopFileWatcher();
            }
        };

        if (Config.WatchFileChanges)
        {
            StartFileWatcher();
        }
    }

    public override void Stop()
    {
        fastUpdateTimer?.Stop();

        var formActMain = ActGlobals.oFormActMain;
        
        if (formActMain is not null)
            formActMain.OnLogLineRead -= OnLogLineRead;
    }

    public override void Dispose()
    {
        fastUpdateTimer?.Dispose();
        base.Dispose();
    }

    protected override void Update()
    {
        // Nothing to do since this is handled in SendFastRateEvents.
    }

    private void OnLogLineRead(bool isImport, LogLineEventArgs args)
    {
        logLinesSemaphore.Wait();
        if (isImport)
            importLogLines.Add(args.logLine);
        else
            logLines.Add(args.logLine);
        logLinesSemaphore.Release();
    }

    // Sends an event called |event_name| to javascript, with an event.detail that contains
    // the fields and values of the |detail| structure.
    public void DispatchToJs(JSEvent detail)
    {
        var ev = new JObject
        {
            ["type"] = detail.EventName(),
            ["detail"] = JObject.FromObject(detail)
        };
        if (IsStateEvent(detail.EventName()))
            DispatchAndCacheEvent(ev);
        else
            DispatchEvent(ev);
    }

    private static bool IsStateEvent(string name) =>
        name is GameExistsEvent or GameActiveChangedEvent or InCombatChangedEvent or ZoneChangedEvent
            or PlayerChangedEvent;

    // 給每個 tick 都可能送的事件用：沒有訂閱者時 DispatchEvent 什麼都不送，先判斷就省掉組 JObject；
    // 有訂閱者時內容不變。只能用在已註冊的事件名（未註冊的名字原本會擲例外，這裡會變成靜默略過）。回傳有沒有送出。
    private bool DispatchToJsIfSubscribed(JSEvent detail)
    {
        if (!HasSubscriber(detail.EventName()))
            return false;
        DispatchToJs(detail);
        return true;
    }

    // 只在快速計時器上呼叫。沒人訂閱而略過的那次變化不在快取裡：作廢快取，等有人訂閱時補送一次。
    private void MarkPlayerEventSent(bool sent)
    {
        if (sent)
        {
            playerEventStale = false;
        }
        else if (!playerEventStale)
        {
            playerEventStale = true;
            eventCache[PlayerChangedEvent] = null;
        }
    }

    // Events that we want to update as soon as possible.  Return next time this should be called.
    private int SendFastRateEvents()
    {
        if (resetNotifyState)
            notifyState = new NotifyState();
        resetNotifyState = false;

        var gameExists = ffxiv.FindProcess();
        if (gameExists != notifyState.GameExists)
        {
            notifyState.GameExists = gameExists;
            DispatchToJs(new JSEvents.GameExistsEvent(gameExists));
        }

        var gameActive = ffxiv.IsActive();
        if (gameActive != notifyState.GameActive)
        {
            notifyState.GameActive = gameActive;
            DispatchToJs(new JSEvents.GameActiveChangedEvent(gameActive));
        }

        // Silently stop sending other messages if the ffxiv process isn't around.
        if (!gameExists)
        {
            return KUberSlowTimerMilli;
        }

        // onInCombatChangedEvent: Fires when entering or leaving combat.
        var inActCombat = ActGlobals.oFormActMain.InCombat;
        var inGameCombat = ffxiv.GetInGameCombat();
        if (!notifyState.InActCombat.HasValue || inActCombat != notifyState.InActCombat ||
            !notifyState.InGameCombat.HasValue || inGameCombat != notifyState.InGameCombat)
        {
            notifyState.InActCombat = inActCombat;
            notifyState.InGameCombat = inGameCombat;
            DispatchToJs(new JSEvents.InCombatChangedEvent(inActCombat, inGameCombat));
        }

        // onZoneChangedEvent: Fires when the player changes their current zone.
        var zoneName = ActGlobals.oFormActMain.CurrentZone;
        if (notifyState.ZoneName == null || !zoneName.Equals(notifyState.ZoneName))
        {
            notifyState.ZoneName = zoneName;
            DispatchToJs(new JSEvents.ZoneChangedEvent(zoneName));
        }

        // The |player| can be null, such as during a zone change.
        var player = ffxiv.GetSelfData();

        // onPlayerDiedEvent: Fires when the player dies. All buffs/debuffs are
        // lost.
        if (player != null)
        {
            var dead = player.hp == 0;
            if (dead != notifyState.Dead)
            {
                notifyState.Dead = dead;
                if (dead)
                    DispatchToJs(new JSEvents.PlayerDiedEvent());
            }
        }

        // onPlayerChangedEvent: Fires when current player data changes.
        if (player != null)
        {
            var send = false;
            if (!player.Equals(notifyState.Player))
            {
                notifyState.Player = player;
                send = true;
            }

            if (playerEventStale && HasSubscriber(PlayerChangedEvent))
                send = true;

            // 職業量表只在真的要送出、或位元組變了要和上次送出的量表比內容時才組成 JObject；
            // 何時算「有變」、送出的內容與每個 tick 都組 JObject 時相同（量表內容只由職業＋這 8 個位元組決定）。
            if (ffxiv.TryReadJobSpecificBytes(out var jobBytes) && ffxiv.HasJobSpecificData(player.job))
            {
                JObject job = null;
                if (!send && !notifyState.IsJobKey(player.job, jobBytes))
                {
                    job = ffxiv.JobSpecificDataFromBytes(player.job, jobBytes);
                    notifyState.JobData ??= ffxiv.JobSpecificDataFromBytes(notifyState.JobKeyJob, notifyState.JobKeyBytes);
                    send = !JToken.DeepEquals(job, notifyState.JobData);
                }

                if (send)
                {
                    var subscribed = HasSubscriber(PlayerChangedEvent);
                    if (subscribed)
                        job ??= ffxiv.JobSpecificDataFromBytes(player.job, jobBytes);
                    notifyState.SetJob(player.job, jobBytes, job);
                    if (subscribed)
                    {
                        var ev = new JSEvents.PlayerChangedEvent(player)
                        {
                            jobDetail = job
                        };
                        DispatchToJs(ev);
                    }

                    MarkPlayerEventSent(subscribed);
                }
            }
            else if (send)
            {
                // No job-specific data.
                MarkPlayerEventSent(DispatchToJsIfSubscribed(new JSEvents.PlayerChangedEvent(player)));
            }
        }

        // onLogEvent: Fires when new combat log events from FFXIV are available. This fires after any
        // more specific events, some of which may involve parsing the logs as well.
        logLinesSemaphore.Wait();
        var logs = logLines;
        logLines = lastLogLines;
        var importLogs = importLogLines;
        importLogLines = lastImportLogLines;

        logLinesSemaphore.Release();

        if (logs.Count > 0)
        {
            DispatchToJsIfSubscribed(new JSEvents.LogEvent(logs));
            logs.Clear();
        }

        lastLogLines = logs;
        lastImportLogLines = importLogs;

        return gameActive ? KFastTimerMilli : KSlowTimerMilli;
    }

    // ILogger implementation.
    public void LogDebug(string format, params object[] args)
    {
        this.Log(LogLevel.Debug, format, args);
    }

    public void LogError(string format, params object[] args)
    {
        this.Log(LogLevel.Error, format, args);
    }

    public void LogWarning(string format, params object[] args)
    {
        this.Log(LogLevel.Warning, format, args);
    }

    public void LogInfo(string format, params object[] args)
    {
        this.Log(LogLevel.Info, format, args);
    }

    private Dictionary<string, string> GetDataFiles(string url)
    {
        // Uri is not smart enough to strip the query args here, so we'll do it manually?
        var idx = url.IndexOf('?');
        if (idx > 0)
            url = url[..idx];

        // If file is a remote pointer, load that file explicitly so that the manifest
        // is relative to the pointed to url and not the local file.
        if (url.StartsWith("file:///"))
        {
            var html = File.ReadAllText(new Uri(url).LocalPath);
            var match = System.Text.RegularExpressions.Regex.Match(
                html, @"<meta http-equiv=""refresh"" content=""0; url=(.*)?""\/?>");
            if (match.Groups.Count > 1)
            {
                url = match.Groups[1].Value;
            }
        }

        // TODO: Reimplement
        // return new Dictionary<string, string>();

        var client = container.Resolve<HttpClient>();

        var dataFilePaths = new List<string>();
        try
        {
            var dataDirManifest = new Uri(new Uri(url), "data/manifest.txt");
            var manifestReader = new StringReader(client.GetStringAsync(dataDirManifest).Result);
            for (var line = manifestReader.ReadLine(); line != null; line = manifestReader.ReadLine())
            {
                line = line.Trim();
                if (line.Length > 0)
                    dataFilePaths.Add(line);
            }
        }
        catch (System.Net.WebException e)
        {
            if (e.Status == System.Net.WebExceptionStatus.ProtocolError &&
                e.Response is System.Net.HttpWebResponse &&
                ((System.Net.HttpWebResponse)e.Response).StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Ignore file not found.
            }
            else if (e.InnerException != null &&
                     (e.InnerException is FileNotFoundException || e.InnerException is DirectoryNotFoundException))
            {
                // Ignore file not found.
            }
            else if (e.InnerException != null && e.InnerException.InnerException != null &&
                     (e.InnerException.InnerException is FileNotFoundException ||
                      e.InnerException.InnerException is DirectoryNotFoundException))
            {
                // Ignore file not found.
            }
            else
            {
                LogError("Unable to read manifest file: " + e.Message);
            }
        }
        catch (Exception e)
        {
            LogError("Unable to read manifest file: " + e.Message);
        }

        if (dataFilePaths.Count > 0)
        {
            var fileData = new Dictionary<string, string>();
            foreach (var dataFilename in dataFilePaths)
            {
                try
                {
                    var filePath = new Uri(new Uri(url), "data/" + dataFilename);
                    fileData[dataFilename] = client.GetStringAsync(filePath).Result;
                    LogInfo("Read file " + dataFilename);
                }
                catch (Exception e)
                {
                    LogError("Unable to read data file: " + e.Message);
                }
            }

            return fileData;
        }

        return null;
    }

    private JObject FetchDataFiles(JObject msg)
    {
        var result = GetDataFiles(msg["source"].ToString());

        var output = new JObject
        {
            ["detail"] = new JObject
            {
                ["files"] = result == null ? null : JObject.FromObject(result)
            }
        };

        return output;
    }

    private Dictionary<string, string> GetLocalUserFiles(string configDir, string overlayName)
    {
        if (string.IsNullOrEmpty(configDir))
            return null;

        // TODO: It's not great to have to load every js and css file in the user dir.
        // But most of the time they'll be short and there won't be many.  JS
        // could attempt to send an overlay name to C# code (and race with the
        // document ready event), but that's probably overkill.
        var userFiles = new Dictionary<string, string>();
        string topDir;
        string subDir = null;
        try
        {
            topDir = new Uri(configDir).LocalPath;
        }
        catch (UriFormatException)
        {
            // This can happen e.g. "http://localhost:8000".  Thanks, Uri constructor.  /o\
            return null;
        }

        // It's important to return null here vs an empty dictionary.  null here
        // indicates to attempt to load the user overloads indirectly via the path.
        // This is how remote user directories work.
        try
        {
            if (!Directory.Exists(topDir))
            {
                return null;
            }

            if (overlayName != null)
            {
                subDir = Path.Combine(topDir, overlayName);
                if (!Directory.Exists(subDir))
                    subDir = null;
            }
        }
        catch (Exception e)
        {
            LogError("Error checking directory: {0}", e.ToString());
            return null;
        }

        try
        {
            overlayName ??= "*";
            var filenames = Directory.EnumerateFiles(topDir, $"{overlayName}.js").Concat(
                Directory.EnumerateFiles(topDir, $"{overlayName}.css"));
            if (subDir != null)
            {
                filenames = filenames.Concat(
                    Directory.EnumerateFiles(subDir, "*.js", SearchOption.AllDirectories)).Concat(
                    Directory.EnumerateFiles(subDir, "*.css", SearchOption.AllDirectories));
            }

            foreach (var filename in filenames)
            {
                //if (filename.Contains("-example."))
                //    continue;
                userFiles[Path.GetRelativePath(topDir, filename)] = File.ReadAllText(filename) +
                                                               $"\n//# sourceURL={filename}";
            }

            var textFilenames = Directory.EnumerateFiles(topDir, "*.txt");
            if (subDir != null)
            {
                textFilenames =
                    textFilenames.Concat(Directory.EnumerateFiles(subDir, "*.txt", SearchOption.AllDirectories));
            }

            foreach (var filename in textFilenames)
            {
                userFiles[Path.GetRelativePath(topDir, filename)] = File.ReadAllText(filename);
            }
        }
        catch (Exception e)
        {
            LogError("User error file exception: {0}", e.ToString());
        }

        return userFiles;
    }


    private void GetUserConfigDirAndFiles(string overlayName, out string configDir, out Dictionary<string, string> localFiles)
    {
        localFiles = null;
        configDir = null;

        if (!string.IsNullOrEmpty(Config.UserConfigFile))
        {
            // Explicit user config directory specified.
            configDir = Config.UserConfigFile;
            localFiles = GetLocalUserFiles(configDir, overlayName);
            return;
        }
        try
        {
            configDir = Path.Combine(container.Resolve<PluginMain>().ConfigPath, "cactbot_user");
            Directory.CreateDirectory(configDir);
            localFiles = GetLocalUserFiles(configDir, overlayName);
        }
        catch (Exception e)
        {
            LogError("Error creating cactbot_user dir: {0}: {1}", configDir, e.ToString());
            configDir = null;
            localFiles = null;
        }
    }

    private JObject FetchUserFiles(JObject msg)
    {
        var overlayName = msg.ContainsKey("overlayName") ? msg["overlayName"].ToString() : null;
        GetUserConfigDirAndFiles(overlayName, out var configDir, out var userFiles);

        var response = new JObject
        {
            ["detail"] = new JObject
            {
                ["userLocation"] = configDir,
                ["localUserFiles"] = userFiles == null ? null : JObject.FromObject(userFiles),
                ["parserLanguage"] = language,
                ["systemLocale"] = pcLocale,
                ["displayLanguage"] = Config.DisplayLanguage,
                // For backwards compatibility:
                ["language"] = language
            }
        };
        return response;
    }

    private void StartFileWatcher()
    {
        watchers = new List<FileSystemWatcher>();
        var paths = new List<string> { Config.UserConfigFile };

        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path))
                continue;

            string watchDir;
            try
            {
                // Get canonical url for paths so that Directory.Exists will work properly.
                watchDir = Path.GetFullPath(Path.GetDirectoryName(new Uri(path).LocalPath)!);
            }
            catch
            {
                continue;
            }

            if (!Directory.Exists(watchDir))
                continue;

            var watcher = new FileSystemWatcher()
            {
                Path = watchDir,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = true,
            };

            // We only care about file changes. New or renamed files don't matter if we don't have a reference to them
            // and adding a new reference causes an existing file to change.
            // 跑在 IO 完成回呼上，漏出去的例外＝整個遊戲結束 ⇒ 整段接住、整個插件生命週期只記第一次。
            watcher.Changed += (_, e) =>
            {
                try
                {
                    DispatchEvent(JObject.FromObject(new
                    {
                        type = "onUserFileChanged",
                        file = e.FullPath,
                    }));
                }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref fileWatcherFailureLogged, 1) != 0)
                        return;
                    try
                    {
                        Log(LogLevel.Error, $"User file watcher failed; later failures are not logged: {ex}");
                    }
                    catch { }
                }
            };

            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);

            LogInfo("Started watching {0}", watchDir);
        }
    }

    private void StopFileWatcher()
    {
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        watchers = null;
    }

    // State that is tracked and sent to JS when it changes.
    private class NotifyState
    {
        public bool AddedDomContentListener = false;
        public bool DomContentLoaded = false;
        public bool SentDataDir = false;
        public bool GameExists;
        public bool GameActive;
        public bool? InActCombat;
        public bool? InGameCombat;
        public bool Dead;
        public string ZoneName;
        public JObject JobData = new();
        public FFXIVProcess.EntityData Player;

        // JobKeyValid 時 JobData 是由 (JobKeyJob, JobKeyBytes) 組出的量表；JobData 為 null＝還沒組，要比對時才組。
        public bool JobKeyValid;
        public FFXIVProcess.EntityJob JobKeyJob;
        public ulong JobKeyBytes;

        public bool IsJobKey(FFXIVProcess.EntityJob job, ulong bytes) =>
            JobKeyValid && JobKeyJob == job && JobKeyBytes == bytes;

        public void SetJob(FFXIVProcess.EntityJob job, ulong bytes, JObject built)
        {
            JobData = built;
            JobKeyValid = true;
            JobKeyJob = job;
            JobKeyBytes = bytes;
        }
    }

    private NotifyState notifyState = new();
}
