using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Advanced_Combat_Tracker;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin.WebSocket;

namespace RainbowMage.OverlayPlugin.EventSources
{
    partial class MiniParseEventSource : EventSourceBase
    {
        private List<string> importedLogs = new List<string>();

        private const string CombatDataEvent = "CombatData";
        private const string ImportedLogLinesEvent = "ImportedLogLines";
        private const string BroadcastMessageEvent = "BroadcastMessage";

        // Event Source

        public BuiltinEventConfig Config { get; set; }

        public MiniParseEventSource(TinyIoCContainer container) : base(container)
        {
            Name = "MiniParse";

            RegisterEventTypes(new List<string>
            {
                CombatDataEvent,
                ImportedLogLinesEvent,
                BroadcastMessageEvent,
            });

            RegisterEventHandler("saveData", (msg) =>
            {
                var key = msg["key"]?.ToString();
                if (key == null)
                    return null;

                Config.OverlayData[key] = msg["data"];
                return null;
            });

            RegisterEventHandler("loadData", (msg) =>
            {
                var key = msg["key"]?.ToString();
                if (key == null)
                    return null;

                if (!Config.OverlayData.ContainsKey(key))
                    return null;

                var ret = new JObject();
                ret["key"] = key;
                ret["data"] = Config.OverlayData[key];
                return ret;
            });

            RegisterEventHandler("say", (msg) =>
            {
                var text = msg["text"]?.ToString();
                if (text == null)
                    return null;

                ActGlobals.oFormActMain.TTS(text);
                return null;
            });

            RegisterEventHandler("playSound", (msg) =>
            {
                var file = msg["file"]?.ToString();
                if (file == null)
                    return null;

                ActGlobals.oFormActMain.PlaySound(file);
                return null;
            });

            RegisterEventHandler("broadcast", (msg) =>
            {
                if (!msg.ContainsKey("msg") || !msg.ContainsKey("source"))
                {
                    Log(LogLevel.Error,
                        "Called broadcast handler without specifying a source or message (\"source\" or \"msg\" property are missing).");
                    return null;
                }

                if (msg["source"].Type != JTokenType.String)
                {
                    Log(LogLevel.Error, "The source passed to the broadcast handler must be a string!");
                    return null;
                }

                DispatchEvent(JObject.FromObject(new
                {
                    type = BroadcastMessageEvent,
                    source = msg["source"],
                    msg = msg["msg"],
                }));

                return null;
            });

            RegisterEventHandler("openWebsiteWithWS", (msg) =>
            {
                var result = new JObject();

                if (!msg.ContainsKey("url"))
                {
                    Log(LogLevel.Error,
                        "Called openWebsiteWithWS handler without specifying a URL (\"url\" property is missing).");
                    result["$error"] =
                        "Called openWebsiteWithWS handler without specifying a URL (\"url\" property is missing).";
                    return result;
                }

                var wsServer = container.Resolve<ServerController>();

                if (!wsServer.Running)
                {
                    result["$error"] = "WSServer is not running";
                    return result;
                }

                try
                {
                    var url = wsServer.GetModernUrl(msg["url"].ToString());
                    var proc = new Process();
                    proc.StartInfo.Verb = "open";
                    proc.StartInfo.FileName = url;
                    proc.Start();
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, $"Failed to to open website: {ex}");
                    result["$error"] = $"Failed to to open website: {ex}";
                    return result;
                }

                result["success"] = true;
                return result;
            });

            ActGlobals.oFormActMain.BeforeLogLineRead +=
                (bool isImport, LogLineEventArgs logInfo) =>
                {
                    if (isImport)
                    {
                        lock (importedLogs)
                        {
                            importedLogs.Add(logInfo.originalLogLine);
                        }
                    }
                };
        }

        private void StopACTCombat()
        {
            ActGlobals.oFormActMain.Invoke((Action)(() => { ActGlobals.oFormActMain.EndCombat(true); }));
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
        }

        protected override void Update()
        {
            var importing = false;

            if (CheckIsActReady() && (!importing || this.Config.UpdateDpsDuringImport))
            {
                if (!HasSubscriber(CombatDataEvent))
                {
                    return;
                }

                // There used to be logic here to skip updating if the encounter info hasn't changed, but that's been commented
                // out since https://github.com/ngld/OverlayPlugin/commit/a56c44e85f0a5608d4185d67e13690dbad461523
                // Probably an unintentional change but it didn't break anything :shrug:
                // 仍然每次都送（晚訂閱的疊加層才收得到）；只有戰鬥結束後內容確定沒變時，改送上一份收集結果。

                var idle = TakeIdleCombatData(out var fresh);
                if (idle != null)
                {
                    try
                    {
                        DispatchCombatData(CombatDataEvent, idle.Snapshot);
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref idleCombatData, idle, null);
                    }
                }
                else
                {
                    var data = this.CollectCombatData();
                    if (data == null)
                        DispatchEvent(new JObject());
                    else
                    {
                        DispatchCombatData(CombatDataEvent, data);
                        if (fresh != null)
                        {
                            fresh.Snapshot = data;
                            Volatile.Write(ref idleCombatData, fresh);
                        }
                    }
                }
            }

            if (importing && HasSubscriber(ImportedLogLinesEvent))
            {
                List<string> logs = null;

                lock (importedLogs)
                {
                    if (importedLogs.Count > 0)
                    {
                        logs = importedLogs;
                        importedLogs = new List<string>();
                    }
                }

                if (logs != null)
                {
                    DispatchEvent(JObject.FromObject(new
                    {
                        type = ImportedLogLinesEvent,
                        logLines = logs
                    }));
                }
            }
        }

        // FFXIV_ACT_Plugin 的 Last10／30／60DPS 以 LastKnownTime 往回取招式，戰鬥結束後仍隨 log 時間遞減；
        // 最後一下傷害早於「LastKnownTime − 這個長度」之後就不再變。取 180 秒，留給 Last180DPS 這類更長的視窗。
        private static readonly TimeSpan LastDpsWindow = TimeSpan.FromSeconds(180);

        // 只有一條執行緒持有：取用時換成 null，送完才放回。
        private IdleCombatData idleCombatData;

        // 一份收集結果與它依賴的全部輸入。戰鬥不在進行中時，收集的輸出只由這些決定
        // （ActGlobals 的 longDuration 等顯示選項執行期沒有人寫，不列）。
        private sealed class IdleCombatData
        {
            public EncounterData Encounter;
            public int ChangeCount;
            public string SortKey;
            public bool SortDesc;
            public Dictionary<string, EncounterData.TextExportFormatter> EncounterVariables;
            public int EncounterVariableCount;
            public Dictionary<string, CombatantData.TextExportFormatter> CombatantVariables;
            public int CombatantVariableCount;
            public Func<JsonSerializerSettings> JsonDefaults;
            public DateTime LastKnownTime;
            public DateTime SettledAt;
            public CombatDataSnapshot Snapshot;
        }

        // 回非 null＝可原樣重送的上一份（呼叫端送完要放回）。回 null 時 fresh 是這次收集要記下的輸入；
        // 戰鬥進行中、或讀不到遭遇時 fresh 也是 null（一律重新收集、不留）。ChangeCount 一定先於其他輸入讀取。
        private IdleCombatData TakeIdleCombatData(out IdleCombatData fresh)
        {
            fresh = null;
            var previous = Interlocked.Exchange(ref idleCombatData, null);
            var form = ActGlobals.oFormActMain;
            var encounter = form?.ActiveZone?.ActiveEncounter;
            if (encounter == null) return null;

            var changeCount = encounter.ChangeCount;
            if (encounter.Active) return null;

            var sortKey = Config.SortKey;
            var sortDesc = Config.SortDesc;
            var encounterVariables = EncounterData.ExportVariables;
            var combatantVariables = CombatantData.ExportVariables;
            var jsonDefaults = JsonConvert.DefaultSettings;
            var lastKnownTime = form.LastKnownTime;

            if (previous != null &&
                ReferenceEquals(previous.Encounter, encounter) &&
                previous.ChangeCount == changeCount &&
                string.Equals(previous.SortKey, sortKey, StringComparison.Ordinal) &&
                previous.SortDesc == sortDesc &&
                ReferenceEquals(previous.EncounterVariables, encounterVariables) &&
                previous.EncounterVariableCount == encounterVariables.Count &&
                ReferenceEquals(previous.CombatantVariables, combatantVariables) &&
                previous.CombatantVariableCount == combatantVariables.Count &&
                ReferenceEquals(previous.JsonDefaults, jsonDefaults) &&
                (previous.LastKnownTime == lastKnownTime ||
                 (previous.LastKnownTime >= previous.SettledAt && lastKnownTime >= previous.SettledAt)))
            {
                return previous;
            }

            fresh = new IdleCombatData
            {
                Encounter = encounter,
                ChangeCount = changeCount,
                SortKey = sortKey,
                SortDesc = sortDesc,
                EncounterVariables = encounterVariables,
                EncounterVariableCount = encounterVariables.Count,
                CombatantVariables = combatantVariables,
                CombatantVariableCount = combatantVariables.Count,
                JsonDefaults = jsonDefaults,
                LastKnownTime = lastKnownTime,
                SettledAt = SettledAt(encounter),
            };
            return null;
        }

        // 讀不完整（例：同時有招式寫入）就回 MaxValue＝永不算穩定，LastKnownTime 一變就重新收集。
        private static DateTime SettledAt(EncounterData encounter)
        {
            try
            {
                var last = DateTime.MinValue;
                foreach (var ally in encounter.GetAllies())
                {
                    if (!ally.Items.TryGetValue(CombatantData.DamageTypeDataOutgoingDamage, out var outgoing) ||
                        !outgoing.Items.TryGetValue("All", out var all))
                        continue;

                    var swings = all.Items;
                    for (var i = 0; i < swings.Count; i++)
                    {
                        if (swings[i].Time > last) last = swings[i].Time;
                    }
                }

                return last == DateTime.MinValue ? DateTime.MinValue : last + LastDpsWindow;
            }
            catch (Exception)
            {
                return DateTime.MaxValue;
            }
        }

        internal JObject CreateCombatData()
        {
            return CollectCombatData()?.ToJObject() ?? new JObject();
        }

        // null 對應原本回傳空 JObject 的情況。
        private CombatDataSnapshot CollectCombatData()
        {
            if (!CheckIsActReady())
            {
                return null;
            }

#if DEBUG
            var stopwatch = new Stopwatch();
            stopwatch.Start();
#endif

            var allies = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.GetAllies();
            Dictionary<string, string> encounter = null;
            List<KeyValuePair<CombatantData, Dictionary<string, string>>> combatant = null;


            encounter = GetEncounterDictionary(allies);
            combatant = GetCombatantList(allies);

            if (encounter == null || combatant == null) return null;

            if (this.Config.SortKey != null && this.Config.SortKey != "")
            {
                int factor = this.Config.SortDesc ? -1 : 1;
                var key = this.Config.SortKey;

                try
                {
                    combatant.Sort((a, b) =>
                    {
                        try
                        {
                            var aValue = float.Parse(a.Value[key]);
                            var bValue = float.Parse(b.Value[key]);

                            return factor * aValue.CompareTo(bValue);
                        }
                        catch (FormatException)
                        {
                            return 0;
                        }
                        catch (KeyNotFoundException)
                        {
                            return 0;
                        }
                    });
                }
                catch (Exception e)
                {
                    Log(LogLevel.Error, Resources.ListSortFailed, key, e);
                }
            }

            var isActive = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter?.Active == true ? "true" : "false";

#if DEBUG
            stopwatch.Stop();
            Log(LogLevel.Trace, "CreateUpdateScript: {0} msec", stopwatch.Elapsed.TotalMilliseconds);
#endif
            return new CombatDataSnapshot(encounter, combatant, isActive);
        }

        private List<KeyValuePair<CombatantData, Dictionary<string, string>>> GetCombatantList(
            List<CombatantData> allies)
        {
#if TRACE
            var stopwatch = new Stopwatch();
            stopwatch.Start();
#endif

            Dictionary<string, string> BuildCombatantDict(CombatantData ally)
            {
                // 先配好容量：只加不刪的字典列舉順序就是加入順序，與容量無關。
                var valueDict = new Dictionary<string, string>(CombatantData.ExportVariables.Count);
                foreach (var exportValuePair in CombatantData.ExportVariables)
                {
                    try
                    {
                        // NAME タグには {NAME:8} のようにコロンで区切られたエクストラ情報が必要で、
                        // プラグインの仕組み的に対応することができないので除外する
                        if (exportValuePair.Key.StartsWith("NAME"))
                        {
                            continue;
                        }

                        // ACT_FFXIV_Plugin が提供する LastXXDPS は、
                        // ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items に All キーが存在しない場合に、
                        // プラグイン内で例外が発生してしまい、パフォーマンスが悪化するので代わりに空の文字列を挿入する
                        if (exportValuePair.Key == "Last10DPS" || exportValuePair.Key == "Last30DPS" || exportValuePair.Key == "Last60DPS" || exportValuePair.Key == "Last180DPS")
                        {
                            if (!ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items.ContainsKey("All"))
                            {
                                valueDict.Add(exportValuePair.Key, "");
                                continue;
                            }
                        }

                        var value = exportValuePair.Value.GetExportString(ally, "");
                        valueDict.Add(exportValuePair.Key, value);
                    }
                    catch (Exception e)
                    {
                        Log(LogLevel.Debug, "GetCombatantList: {0}: {1}: {2}", ally.Name, exportValuePair.Key, e);
                    }
                }
                return valueDict;
            }

            // 循序：人數少、每秒一次，PLINQ 的排程配置比工作本身貴。
            // 結果照 allies 的順序；例外直接拋出、不包成 AggregateException（唯一的呼叫鏈以 catch (Exception) 收）。
            var combatantList = new List<KeyValuePair<CombatantData, Dictionary<string, string>>>(allies.Count);
            for (var i = 0; i < allies.Count; i++)
            {
                var combatantData = allies[i];
                combatantList.Add(new KeyValuePair<CombatantData, Dictionary<string, string>>(
                                      combatantData, BuildCombatantDict(combatantData)));
            }

#if TRACE
            stopwatch.Stop();
            Log(LogLevel.Trace, "GetCombatantList: {0} msec", stopwatch.Elapsed.TotalMilliseconds);
#endif

            return combatantList;
        }

        private Dictionary<string, string> GetEncounterDictionary(List<CombatantData> allies)
        {
#if TRACE
            var stopwatch = new Stopwatch();
            stopwatch.Start();
#endif

            var encounterDict = new Dictionary<string, string>(EncounterData.ExportVariables.Count);
            foreach (var exportValuePair in EncounterData.ExportVariables)
            {
                try
                {
#if TRACE
                    stopwatch.Reset();
                    stopwatch.Start();
#endif

                    // ACT_FFXIV_Plugin が提供する LastXXDPS は、
                    // ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items に All キーが存在しない場合に、
                    // プラグイン内で例外が発生してしまい、パフォーマンスが悪化するので代わりに空の文字列を挿入する
                    if (exportValuePair.Key == "Last10DPS" ||
                        exportValuePair.Key == "Last30DPS" ||
                        exportValuePair.Key == "Last60DPS" ||
                        exportValuePair.Key == "Last180DPS")
                    {
                        if (!allies.All((ally) => ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items
                                                      .ContainsKey("All")))
                        {
                            encounterDict.Add(exportValuePair.Key, "");
                            continue;
                        }
                    }

                    var value = exportValuePair.Value.GetExportString(
                        ActGlobals.oFormActMain.ActiveZone.ActiveEncounter,
                        allies,
                        "");
                    encounterDict.Add(exportValuePair.Key, value);
                }
                catch (Exception e)
                {
                    Log(LogLevel.Debug, "GetEncounterDictionary: {0}: {1}", exportValuePair.Key, e);
                }
            }

#if TRACE
            stopwatch.Stop();
            Log(LogLevel.Trace, "GetEncounterDictionary: {0} msec", stopwatch.Elapsed.TotalMilliseconds);
#endif

            return encounterDict;
        }

        private static bool CheckIsActReady()
        {
            if (ActGlobals.oFormActMain?.ActiveZone?.ActiveEncounter != null &&
                EncounterData.ExportVariables != null &&
                CombatantData.ExportVariables != null)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}
