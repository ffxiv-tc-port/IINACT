using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Game;
using Microsoft.Extensions.ObjectPool;
using RainbowMage.OverlayPlugin.MemoryProcessors.Aggro;

namespace RainbowMage.OverlayPlugin.MemoryProcessors.Combatant
{
    public interface ICombatantMemory : IVersionedMemory
    {
        Combatant GetSelfCombatant();
        Combatant GetCombatantFromAddress(IntPtr address, uint selfCharID);
        List<Combatant> GetCombatantList();
        // 與 GetCombatantList() 同一份清單，只有 Effects 不保證有內容；呼叫端不可讀 Effects。
        List<Combatant> GetCombatantListWithoutEffects();
        // 同上；新物件可先從 recycle 取（null＝照常 new）。recycle 由呼叫端獨占，
        // 只能放回「這份清單裡、之後不會再被任何人碰」的物件。
        List<Combatant> GetCombatantListWithoutEffects(ObjectPool<Combatant> recycle);
        // 同上，但結果填進呼叫端給的 into（先清空、不另配清單）；into 由呼叫端獨占。
        void GetCombatantListWithoutEffects(ObjectPool<Combatant> recycle, List<Combatant> into);
        void ReturnCombatant(Combatant combatant);
    }

    public class CombatantMemoryManager : ICombatantMemory
    {
        private readonly TinyIoCContainer container;
        private readonly FFXIVRepository repository;
        private readonly ILogger logger;
        private ICombatantMemory memory = null;

        public CombatantMemoryManager(TinyIoCContainer container)
        {
            this.container = container;
            logger = container.Resolve<ILogger>();
            container.Register<ICombatantMemory70, CombatantMemory70>();
            container.Register<ICombatantMemory71, CombatantMemory71>();
            container.Register<ICombatantMemory72, CombatantMemory72>();
            container.Register<ICombatantMemory73, CombatantMemory73>();
            container.Register<ICombatantMemory74, CombatantMemory74>();
            container.Register<ICombatantMemory75, CombatantMemory75>();
            repository = container.Resolve<FFXIVRepository>();

            var memory = container.Resolve<FFXIVMemory>();
            memory.RegisterOnProcessChangeHandler(FindMemory);
        }

        private void FindMemory(object sender, Process p)
        {
            memory = null;
            if (p == null)
            {
                return;
            }

            ScanPointers();
        }

        public void ScanPointers()
        {
            // 台服：候選由遊戲碼裡的版面指紋決定，不走「版本號最高且特徵碼找得到」——特徵碼不驗版面，
            // 上游新增的版本會靜默接管。70～72、75 對台服都不是正確版面，不列入。
            var layout = DetectLayout(out var fingerprint);
            List<ICombatantMemory> candidates = new List<ICombatantMemory>();
            switch (layout)
            {
                case CharacterLayout.Gen73:
                    candidates.Add(container.Resolve<ICombatantMemory73>());
                    break;
                case CharacterLayout.Gen74:
                    logger.Log(LogLevel.Warning,
                               "CombatantMemoryManager: game code matches the 7.4 combatant layout, which has not been verified on the TC client; using CombatantMemory74 ({0})",
                               fingerprint);
                    Echo("combatant-layout-74", false,
                         "[IINACT] 提醒：遊戲改版後，戰鬥對象改用台服還沒驗證過的資料版面讀取，疊加層顯示的目標、狀態效果、詠唱等可能不準。詳情請看 /xllog。");
                    candidates.Add(container.Resolve<ICombatantMemory74>());
                    break;
                default:
                    memory = null;
                    logger.Log(LogLevel.Error,
                               "CombatantMemoryManager: combatant memory layout does not match any known layout (likely a game update); combatant memory reading is disabled ({0})",
                               fingerprint);
                    Echo("combatant-layout-unknown", true,
                         "[IINACT] 遊戲改版後戰鬥對象的資料版面對不上，已停用戰鬥對象讀取，疊加層會讀不到目標、狀態效果、詠唱、名稱等資料。詳情請看 /xllog。");
                    return;
            }

            memory = FFXIVMemory.FindCandidate(candidates, repository.GetMachinaRegion());
            // Info level on purpose: per-candidate "Found combatant memory via X" lines above tell us
            // which candidates validated, but not which one FindCandidate actually selected - and that
            // selection decides whether the TC client reads combatant fields at the right offsets.
            logger.Log(LogLevel.Info, "CombatantMemoryManager: selected {0} (region {1}; {2})",
                       memory == null ? "no candidate" : memory.GetType().Name,
                       repository.GetMachinaRegion(), fingerprint);
        }

        // 只在聊天視窗補一則，log 那行照寫；容器裡沒有 ChatEcho（例：離線探針）就只留 log。
        private void Echo(string key, bool isError, string message)
        {
            if (container.TryResolve<ChatEcho>(out var echo))
                echo.Post(key, message, isError);
        }

        private CharacterLayout DetectLayout(out string fingerprint)
        {
            if (!container.TryResolve<ISigScanner>(out var scanner))
            {
                fingerprint = "no ISigScanner registered";
                return CharacterLayout.Unknown;
            }

            return CharacterLayoutFingerprint.Detect(scanner, out fingerprint);
        }

        public bool IsValid()
        {
            if (memory == null || !memory.IsValid())
            {
                return false;
            }

            return true;
        }

        Version IVersionedMemory.GetVersion()
        {
            if (!IsValid())
                return null;
            return memory.GetVersion();
        }

        public Combatant GetCombatantFromAddress(IntPtr address, uint selfCharID)
        {
            if (!IsValid())
            {
                return null;
            }

            return memory.GetCombatantFromAddress(address, selfCharID);
        }

        public List<Combatant> GetCombatantList()
        {
            if (!IsValid())
            {
                return new List<Combatant>();
            }

            return memory.GetCombatantList();
        }

        public List<Combatant> GetCombatantListWithoutEffects()
        {
            if (!IsValid())
            {
                return new List<Combatant>();
            }

            return memory.GetCombatantListWithoutEffects();
        }

        public List<Combatant> GetCombatantListWithoutEffects(ObjectPool<Combatant> recycle)
        {
            if (!IsValid())
            {
                return new List<Combatant>();
            }

            return memory.GetCombatantListWithoutEffects(recycle);
        }

        public void GetCombatantListWithoutEffects(ObjectPool<Combatant> recycle, List<Combatant> into)
        {
            if (!IsValid())
            {
                into.Clear();
                return;
            }

            memory.GetCombatantListWithoutEffects(recycle, into);
        }

        public Combatant GetSelfCombatant()
        {
            if (!IsValid())
            {
                return null;
            }

            return memory.GetSelfCombatant();
        }
        
        public void ReturnCombatant(Combatant combatant)
        {
            if (!IsValid())
            {
                return;
            }

            memory.ReturnCombatant(combatant);
        }
    }
}
