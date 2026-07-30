using System;
using System.Collections.Generic;
using System.Diagnostics;
using RainbowMage.OverlayPlugin.MemoryProcessors.Aggro;

namespace RainbowMage.OverlayPlugin.MemoryProcessors.Combatant
{
    public interface ICombatantMemory : IVersionedMemory
    {
        Combatant GetSelfCombatant();
        Combatant GetCombatantFromAddress(IntPtr address, uint selfCharID);
        List<Combatant> GetCombatantList();
        void ReturnCombatant(Combatant combatant);
    }

    public class CombatantMemoryManager : ICombatantMemory
    {
        private readonly TinyIoCContainer container;
        private readonly FFXIVRepository repository;
        private ICombatantMemory memory = null;

        public CombatantMemoryManager(TinyIoCContainer container)
        {
            this.container = container;
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
            List<ICombatantMemory> candidates = new List<ICombatantMemory>();
            candidates.Add(container.Resolve<ICombatantMemory70>());
            candidates.Add(container.Resolve<ICombatantMemory71>());
            candidates.Add(container.Resolve<ICombatantMemory72>());
            candidates.Add(container.Resolve<ICombatantMemory73>());
            candidates.Add(container.Resolve<ICombatantMemory74>());
            // TC fork: CombatantMemory75 is deliberately NOT offered as a candidate.
            // Upstream 00860a6 moved CastActionType from 0x2792 to 0x2791, which is correct
            // for a 7.5-generation client but wrong for the TC 7.20 client (a 7.3 generation).
            // Verified offline against our own FFXIVClientStructs 7.20.0.0: CastInfo sits at
            // 0x2790 (anchored by 9 consecutive field matches -- ActionId +0x4 = CastBuffID
            // 0x2794, TargetId +0x10 = 0x27A0, TargetLocation +0x20 = 0x27B0,
            // CurrentCastTime +0x34 = 0x27C4), and within CastInfo the layout is
            // IsCasting +0x0 / Interruptible +0x1 / ActionType +0x2.
            // So on TC, ActionType really is at 0x2792 (= CombatantMemory74) and 0x2791 would
            // read Interruptible instead. Registering 75 would silently win the candidate walk
            // (FindCandidate targets tcVersion 99.0 and 75 sorts last), so it is left out.
            // Re-enable only if the TC client is ever confirmed to move to the 7.5 layout.
            memory = FFXIVMemory.FindCandidate(candidates, repository.GetMachinaRegion());
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
