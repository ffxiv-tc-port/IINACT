using System;
using System.Collections.Generic;
using Advanced_Combat_Tracker;
using Newtonsoft.Json.Linq;

namespace RainbowMage.OverlayPlugin.EventSources
{
    /**
     * This EventSource contains lines that should work without FFXIV, but add additional information when FFXIV is present
     */
    partial class FFXIVOptionalEventSource : EventSourceBase
    {
        private const string LogLineEvent = "LogLine";
        private const string ChangeZoneEvent = "ChangeZone";
        private const string ChangeMapEvent = "ChangeMap";
        private const string GameVersionEvent = "GameVersion";
        private const string ChangePrimaryPlayerEvent = "ChangePrimaryPlayer";

        private FFXIVRepository repository;

        // 保護 ChangeZone／ChangePrimaryPlayer 快取的「檢查後寫入」：記錄行執行緒與 Start() 的重播各寫一邊。派送一律在鎖外。
        private readonly object stateCacheLock = new();

        // 與原本 JObject.FromObject(new { type, line, rawLine }) 同一個匿名型別形狀；line 沒拆過時在這裡才拆。
        private static readonly Func<string, string[], JObject> BuildLogLineEvent =
            (rawLine, line) => JObject.FromObject(new
            {
                type = LogLineEvent,
                line = line ?? rawLine.Split('|'),
                rawLine,
            });

        // Event Source

        public BuiltinEventConfig Config { get; set; }

        public FFXIVOptionalEventSource(TinyIoCContainer container) : base(container)
        {
            Name = "FFXIVOptional";
            var haveRepository = container.TryResolve(out repository);

            RegisterEventTypes(new List<string>
            {
                LogLineEvent,
            });

            if (haveRepository)
            {
                // These events need to deliver cached values to new subscribers.
                RegisterCachedEventTypes(new List<string>
                {
                    ChangePrimaryPlayerEvent,
                    ChangeZoneEvent,
                    ChangeMapEvent,
                    GameVersionEvent,
                });
            }

            ActGlobals.oFormActMain.BeforeLogLineRead += LogLineHandler;
        }

        private void StopACTCombat()
        {
            ActGlobals.oFormActMain.Invoke((Action)(() => { ActGlobals.oFormActMain.EndCombat(true); }));
        }

        private void LogLineHandler(bool isImport, LogLineEventArgs args)
        {
            if (isImport)
            {
                try
                {
                    LogMessageType lineType = (LogMessageType)args.detectedType;

                    // If an imported log has split the encounter, also split it while importing.
                    // TODO: should we also consider the current user's wipe config option here for splitting,
                    // even if the original log writer did not have it set to true?
                    if (lineType == LogMessageType.InCombat)
                    {
                        // @TODO: Should this be a customizable setting so that it can be changed per game, or somehow allow
                        // downstream plugins to override it to change it per game?
                        var line = args.originalLogLine.Split('|');

                        var inACTCombat = Convert.ToUInt32(line[2]);
                        if (inACTCombat == 0)
                        {
                            StopACTCombat();
                        }
                    }
                }
                catch
                {
                    return;
                }

                return;
            }

            try
            {
                LogMessageType lineType = (LogMessageType)args.detectedType;
                var rawLine = args.originalLogLine ?? throw new NullReferenceException(); // 與原本先 Split 時一樣：記錯誤、不派送
                // 只有下面用到欄位的幾種行才在這裡拆；其餘的行由 BuildLogLineEvent 在有人要 JObject 時才拆。
                string[] line = null;

                switch (lineType)
                {
                    case LogMessageType.ChangeZone:
                        line = rawLine.Split('|');
                        if (line.Length < 3) return;

                        var zoneID = Convert.ToUInt32(line[2], 16);
                        var zoneName = line[3];

                        CacheAndDispatchState(BuildChangeZoneEvent(zoneID, zoneName));
                        break;

                    case LogMessageType.ChangeMap:
                        line = rawLine.Split('|');
                        if (line.Length < 6) return;

                        var mapID = Convert.ToUInt32(line[2], 10);
                        var regionName = line[3];
                        var placeName = line[4];
                        var placeNameSub = line[5];

                        DispatchAndCacheEvent(JObject.FromObject(new
                        {
                            type = ChangeMapEvent,
                            mapID,
                            regionName,
                            placeName,
                            placeNameSub
                        }));
                        break;

                    case LogMessageType.ChangePrimaryPlayer:
                        line = rawLine.Split('|');
                        if (line.Length < 4) return;

                        var charID = Convert.ToUInt32(line[2], 16);
                        var charName = line[3];

                        CacheAndDispatchState(BuildChangePrimaryPlayerEvent(charID, charName));
                        break;

                    case LogMessageType.Network6D:
                        if (!Config.EndEncounterAfterWipe) break;
                        line = rawLine.Split('|');
                        if (line.Length < 4) break;

                        // 4000000F is the new value for 6.2, 40000010 is the pre-6.2 value.
                        // When CN/KR is on 6.2, this can be removed.
                        if (line[3] == "40000010" || line[3] == "4000000F")
                        {
                            StopACTCombat();
                        }

                        break;
                    case LogMessageType.Process:
                        var gameVersion = repository.GetGameVersion();
                        DispatchAndCacheEvent(JObject.FromObject(new
                        {
                            type = GameVersionEvent,
                            gameVersion
                        }));
                        break;
                }

                // @TODO: Technically this event should work fine for any game, not just FFXIV.
                // Should it be moved to emit right after attempting the split instead?
                // 每一行都走這裡：沒有訂閱者時什麼都不組；只有 IRawLogLineReceiver 訂閱時也不組 JObject。
                DispatchLogLine(LogLineEvent, rawLine, line, BuildLogLineEvent);
            }
            catch (Exception e)
            {
                Log(LogLevel.Error, "Failed to process log line: " + e.ToString());
            }
        }

        public override void LoadConfig(IPluginConfig config)
        {
            this.Config = container.Resolve<BuiltinEventConfig>();

            this.Config.UpdateIntervalChanged += (o, e) => { this.Start(); };
        }

        public override void SaveConfig(IPluginConfig config) { }

        public override void Start()
        {
            this.timer.Change(0, this.Config.UpdateInterval * 1000);
            ReplayCurrentState();
        }

        // 解析器只在一開始送一次區域／主角色的記錄行；中途才掛上的 event source 會錯過，要從 repository 補回快取。
        // 只補「還沒有快取」的：一旦收到過記錄行，快取就由記錄行維持；UpdateIntervalChanged 重進 Start() 時不會重送。
        private void ReplayCurrentState()
        {
            if (repository == null)
                return;

            try
            {
                ReplayIfUncached(ChangeZoneEvent, () =>
                {
                    var zoneID = repository.GetCurrentTerritoryID();
                    var zoneName = ActGlobals.oFormActMain?.CurrentZone;
                    // 刻意不送空區名：raidboss 依 zoneID 去重，先收到空名會讓之後同 ID 的真記錄行被忽略。
                    if (zoneID is null or 0 || string.IsNullOrEmpty(zoneName))
                        return null;
                    return BuildChangeZoneEvent(zoneID.Value, zoneName);
                });

                ReplayIfUncached(ChangePrimaryPlayerEvent, () =>
                {
                    var charID = repository.GetPlayerID();
                    var charName = repository.GetPlayerName();
                    if (charID == 0 || string.IsNullOrEmpty(charName))
                        return null;
                    return BuildChangePrimaryPlayerEvent(charID, charName);
                });
            }
            catch (Exception e)
            {
                Log(LogLevel.Warning, "Failed to replay zone/player state: " + e);
            }
        }

        private void ReplayIfUncached(string type, Func<JObject> build)
        {
            if (IsCached(type))
                return;
            // 讀 repository 不持鎖；寫入前在鎖內再判一次，期間到的記錄行（較新）優先。
            var ev = build();
            if (ev == null)
                return;
            lock (stateCacheLock)
            {
                if (IsCached(type))
                    return;
                eventCache[type] = ev;
            }

            DispatchEvent(ev);
        }

        private bool IsCached(string type)
        {
            lock (stateCacheLock)
                return eventCache.TryGetValue(type, out var cached) && cached != null;
        }

        private void CacheAndDispatchState(JObject ev)
        {
            lock (stateCacheLock)
                eventCache[ev["type"].ToString()] = ev;
            DispatchEvent(ev);
        }

        // 記錄行與重播共用：欄位名、順序、型別（uint／string）必須與 cactbot 端解析的一致。
        private static JObject BuildChangeZoneEvent(uint zoneID, string zoneName) =>
            JObject.FromObject(new
            {
                type = ChangeZoneEvent,
                zoneID,
                zoneName,
            });

        private static JObject BuildChangePrimaryPlayerEvent(uint charID, string charName) =>
            JObject.FromObject(new
            {
                type = ChangePrimaryPlayerEvent,
                charID,
                charName,
            });

        protected override void Update() { }
    }
}
