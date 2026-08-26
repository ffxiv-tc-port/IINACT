using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RainbowMage.OverlayPlugin.MemoryProcessors.InCombat;
using RainbowMage.OverlayPlugin.MemoryProcessors.Combatant;
using RainbowMage.OverlayPlugin.MemoryProcessors.ContentFinderSettings;
using MachinaRegion = System.String;
using OpcodeName = System.String;
using OpcodeVersion = System.String;


namespace RainbowMage.OverlayPlugin.NetworkProcessors
{
    using Opcodes = Dictionary<MachinaRegion, Dictionary<OpcodeVersion, Dictionary<OpcodeName, OpcodeConfigEntry>>>;

    class OverlayPluginLogLines
    {
        public OverlayPluginLogLines(TinyIoCContainer container)
        {
            container.Register(new OverlayPluginLogLineConfig(container));
            container.Register(new LineMapEffect(container));
            container.Register(new LineFateControl(container));
            container.Register(new LineCEDirector(container));
            container.Register(new LineInCombat(container));
            container.Register(new LineCombatant(container));
            container.Register(new LineRSV(container));
            container.Register(new LineActorCastExtra(container));
            container.Register(new LineAbilityExtra(container));
            container.Register(new LineContentFinderSettings(container));
            container.Register(new LineNpcYell(container));
            container.Register(new LineBattleTalk2(container));
            container.Register(new LineCountdown(container));
            container.Register(new LineCountdownCancel(container));
            container.Register(new LineActorMove(container));
            container.Register(new LineActorSetPos(container));
            container.Register(new LineSpawnNpcExtra(container));
            container.Register(new LineActorControlExtra(container));
            container.Register(new LineActorControlSelfExtra(container));
        }
    }

    class OverlayPluginLogLineConfig
    {
        private Opcodes config = new();

        private ILogger logger;
        private FFXIVRepository repository;

        private int exceptionCount = 0;
        private const int maxExceptionsLogged = 3;

        // TC fork: ensures the "no opcodes for this game version" explanation is emitted exactly
        // once, independently of the maxExceptionsLogged budget above.
        private bool loggedMissingVersionWarning = false;

        public OverlayPluginLogLineConfig(TinyIoCContainer container)
        {
            logger = container.Resolve<ILogger>();
            repository = container.Resolve<FFXIVRepository>();

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames().Single(str => str.EndsWith("opcodes.jsonc"));
                string jsonData;
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                using (var reader = new StreamReader(stream))
                {
                    jsonData = reader.ReadToEnd();
                }

                config = JsonConvert.DeserializeAnonymousType(jsonData, config);
            }
            catch (Exception ex)
            {
                LogException($"FFXIVCustomLogLines: Failed to load reserved log line: {ex}");
            }
        }
        
        private void LogException(string message)
        {
            if (exceptionCount >= maxExceptionsLogged)
                return;
            exceptionCount++;
            logger.Log(LogLevel.Error, message);
        }
        
        private void LogWarning(string message)
        {
            if (exceptionCount >= maxExceptionsLogged)
                return;
            exceptionCount++;
            logger.Log(LogLevel.Warning, message);
        }

        private IOpcodeConfigEntry GetOpcode(string name, Opcodes opcodes, string version, string opcodeType, MachinaRegion machinaRegion)
        {
            if (opcodes == null)
                return null;

            if (opcodes.TryGetValue(machinaRegion, out var regionOpcodes))
            {
                if (regionOpcodes.TryGetValue(version, out var versionOpcodes))
                {
                    if (versionOpcodes.TryGetValue(name, out var opcode))
                    {
                        return opcode;
                    }

                    LogException($"No {opcodeType} opcode for game region {machinaRegion}, version {version}, opcode name {name}");
                }
                else
                {
                    if (repository.GetMachinaRegion().ToString() == machinaRegion)
                    {
                        // TC fork: the old code logged a bare one-line warning through the
                        // shared maxExceptionsLogged budget, so the user saw 3 truncated,
                        // unexplained copies and had no idea what had actually stopped working.
                        // Log one complete, actionable warning instead (outside that budget),
                        // and keep the per-opcode detail at Debug level.
                        if (!loggedMissingVersionWarning)
                        {
                            loggedMissingVersionWarning = true;
                            var knownVersions = regionOpcodes.Keys.Count > 0
                                ? string.Join(", ", regionOpcodes.Keys)
                                : "(none)";
                            logger.Log(LogLevel.Warning,
                                $"找不到對應的 {opcodeType} opcode:遊戲區域 {machinaRegion}、版本 {version}。" +
                                $"目前只有這些版本的 opcode 資料:{knownVersions}。" +
                                "因此 OverlayPlugin 的自訂網路 log line 全部停用" +
                                "(MapEffect、NpcYell、Countdown、CountdownCancel、RSVData、CEDirector、" +
                                "BattleTalk2、ActorMove、ActorSetPos、SpawnNpcExtra)," +
                                "依賴這些 log line 的懸浮視窗與 cactbot 觸發器不會被觸發。" +
                                "傷害/治療量的統計解析不受影響——那來自 FFXIV_ACT_Plugin,運作正常。" +
                                "opcode 刻意不沿用舊版遊戲的數值:它每次改版都會重新洗牌," +
                                "用錯的 opcode 會產生錯誤的 log line,比不產生更糟。" +
                                "等 opcodes.jsonc 補上這個遊戲版本的資料後就會自動恢復。");
                        }
                        logger.Log(LogLevel.Debug,
                            $"[opcodes] disabled: no {opcodeType} opcode for {machinaRegion}/{version}: {name}");
                    }
                }
            }
            else
            {
                LogException($"No {opcodeType} opcodes for game region {machinaRegion}");
            }

            return null;
        }
        
        public IOpcodeConfigEntry this[string name]
        {
            get
            {
                var machinaRegion = repository.GetMachinaRegion().ToString();
                return this[name, machinaRegion];
            }
        }

        public IOpcodeConfigEntry this[string name, MachinaRegion machinaRegion]
        {
            get
            {
                var version = repository.GetGameVersion();
                if (version == null)
                {
                    LogException("Could not detect game version from FFXIV_ACT_Plugin");
                    return null;
                }
                
                return GetOpcode(name, config, version, "resource", machinaRegion);
            }
        }
    }

    interface IOpcodeConfigEntry
    {
        uint opcode { get; }
        uint size { get; }
    }

    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.DefaultNamingStrategy))]
    class OpcodeConfigEntry : IOpcodeConfigEntry
    {
        public uint opcode { get; set; }
        public uint size { get; set; }
    }
}
