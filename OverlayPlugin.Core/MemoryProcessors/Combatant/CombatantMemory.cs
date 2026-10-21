using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;

namespace RainbowMage.OverlayPlugin.MemoryProcessors.Combatant
{
    public abstract class CombatantMemory : ICombatantMemory
    {
        private FFXIVMemory memory;
        private ILogger logger;

        private IntPtr charmapAddress = IntPtr.Zero;

        private string charmapSignature;

        private int numMemoryCombatants;
        private int combatantSize;
        private int effectSize;
        
        protected ObjectPool<Combatant> combatantPool;

        // 只是下一次結果清單的初始容量；多條執行緒互蓋無妨。
        private int lastListCount;

        private const int NameCacheSets = 512;
        private readonly NameCacheEntry[] nameCache = new NameCacheEntry[NameCacheSets * 2];

        // Constants.
        protected const uint emptyID = 0xE0000000;

        public CombatantMemory(
            TinyIoCContainer container, string charmapSignature, int combatantSize, int effectSize,
            int numMemoryCombatants = 421)
        {
            this.charmapSignature = charmapSignature;
            this.combatantSize = combatantSize;
            this.effectSize = effectSize;
            this.numMemoryCombatants = numMemoryCombatants;
            logger = container.Resolve<ILogger>();
            memory = container.Resolve<FFXIVMemory>();
            var policy = new DefaultPooledObjectPolicy<Combatant>();
            combatantPool = new DefaultObjectPool<Combatant>(policy, maximumRetained: 2 * numMemoryCombatants);
        }

        private void ResetPointers()
        {
            charmapAddress = IntPtr.Zero;
        }

        private bool HasValidPointers()
        {
            if (charmapAddress == IntPtr.Zero)
                return false;
            return true;
        }

        public bool IsValid()
        {
            if (!memory.IsValid())
                return false;

            if (!HasValidPointers())
                return false;

            return true;
        }

        public void ScanPointers()
        {
            ResetPointers();
            if (!memory.IsValid())
                return;

            List<string> fail = new List<string>();

            List<IntPtr> list = memory.SigScan(charmapSignature, 0, true);
            if (list != null && list.Count > 0)
            {
                charmapAddress = list[0];
            }
            else
            {
                charmapAddress = IntPtr.Zero;
                fail.Add(nameof(charmapAddress));
            }

            logger.Log(LogLevel.Debug, "charmapAddress: 0x{0:X}", charmapAddress.ToInt64());

            Combatant c = GetSelfCombatant();
            if (c != null)
            {
                logger.Log(LogLevel.Debug, "MyCharacter: '{0}' (0x{1:X})", c.Name, c.ID);
            }

            if (fail.Count == 0)
            {
                logger.Log(LogLevel.Info, $"Found combatant memory via {GetType().Name}.");
                return;
            }

            logger.Log(LogLevel.Error,
                       $"Failed to find combatant memory via {GetType().Name}: {string.Join(",", fail)}.");
            return;
        }

        public abstract Version GetVersion();

        public Combatant GetSelfCombatant()
        {
            IntPtr address = memory.ReadIntPtr(charmapAddress);
            if (address == IntPtr.Zero)
                return null;
            byte[] source = memory.GetByteArrayPooled(address, combatantSize);
            if (source == null)
                return null;
            var ret = GetCombatantFromByteArray(source, 0, true, true);
            ArrayPool<byte>.Shared.Return(source);
            return ret;
        }

        // 與 GetSelfCombatant()?.ID ?? 0 相同（同樣的指標、同樣讀 combatantSize 個位元組），只是不建物件。
        private unsafe uint ReadSelfID()
        {
            IntPtr address = memory.ReadIntPtr(charmapAddress);
            if (address == IntPtr.Zero)
                return 0;
            byte[] source = memory.GetByteArrayPooled(address, combatantSize);
            if (source == null)
                return 0;
            var id = Unsafe.ReadUnaligned<uint>(ref source[SelfIdOffset]);
            ArrayPool<byte>.Shared.Return(source);
            return id;
        }

        public Combatant GetCombatantFromAddress(IntPtr address, uint selfCharID = 0)
        {
            byte[] c = memory.GetByteArrayPooled(address, combatantSize);
            if (c == null)
                return null;
            var ret = GetCombatantFromByteArray(c, selfCharID, false);
            ArrayPool<byte>.Shared.Return(c);
            return ret;
        }

        public List<Combatant> GetCombatantList()
        {
            return GetCombatantList(false);
        }

        // 契約：筆數、順序、其餘欄位都和 GetCombatantList() 相同，只有 Effects 不保證有內容。
        // 只給不讀 Effects 的呼叫端用。
        public List<Combatant> GetCombatantListWithoutEffects()
        {
            return GetCombatantList(true);
        }

        public List<Combatant> GetCombatantListWithoutEffects(ObjectPool<Combatant> recycle)
        {
            return GetCombatantList(true, recycle);
        }

        public void GetCombatantListWithoutEffects(ObjectPool<Combatant> recycle, List<Combatant> into)
        {
            into.Clear();
            GetCombatantList(true, recycle, into);
        }

        // 子類別若知道自己的 ID 在物件原始位元組的哪個偏移（與 GetCombatantFromByteArray 讀的是同一個欄位），
        // 清單只為了取 ID 時就直接讀，不必整個建出來；-1＝照舊建物件。
        protected virtual int SelfIdOffset => -1;

        private unsafe List<Combatant> GetCombatantList(bool exceptEffects, ObjectPool<Combatant> recycle = null, List<Combatant> into = null)
        {
            // 沒給 into 時每次都是新清單（呼叫端可能留著）；只有容量照上一次的筆數先配好。
            var result = into ?? new List<Combatant>(Volatile.Read(ref lastListCount));
            // 去重用的 ID 表放在呼叫端堆疊上：刻意不用 [ThreadStatic] 快取，這裡跑在執行緒池上，
            // 插件卸載後池執行緒退休會寫壞 GC 堆（dotnet/runtime#135441）。
            Span<uint> seen = numMemoryCombatants <= 1024
                ? stackalloc uint[numMemoryCombatants]
                : new uint[numMemoryCombatants];
            var seenCount = 0;
            Combatant mychar = null;
            uint mycharID;
            if (SelfIdOffset >= 0)
                mycharID = ReadSelfID();
            else
            {
                mychar = GetSelfCombatant();
                mycharID = mychar?.ID ?? 0;
            }

            // Int64 pointer size
            const int sz = 8;
            byte[] source = memory.GetByteArrayPooled(charmapAddress, sz * numMemoryCombatants);
            if (source == null)
            {
                ReturnCombatant(mychar);
                return result;
            }

            for (int i = 0; i < numMemoryCombatants; i++)
            {
                IntPtr p;
                fixed (byte* bp = source) p = new IntPtr(*(Int64*)&bp[i * sz]);

                if (p == IntPtr.Zero)
                    continue;

                byte[] c = memory.GetByteArrayPooled(p, combatantSize);
                if (c == null)
                    continue;
                Combatant combatant = exceptEffects
                    ? GetMobFromByteArrayWithoutEffects(c, mycharID, recycle)
                    : GetMobFromByteArray(c, mycharID);
                ArrayPool<byte>.Shared.Return(c);
                if (combatant == null)
                    continue;
                if (seen.Slice(0, seenCount).Contains(combatant.ID))
                {
                    ReturnCombatant(combatant);
                    continue;
                }

                // TODO: should this just be a dictionary? there are a lot of id lookups.
                result.Add(combatant);
                seen[seenCount++] = combatant.ID;
            }
            
            Volatile.Write(ref lastListCount, result.Count);
            ArrayPool<byte>.Shared.Return(source);
            ReturnCombatant(mychar);
            return result;
        }

        // 結果與 FFXIVMemory.GetStringFromBytes(source, size) 相同：該函式只看這 size 個位元組，
        // 所以位元組逐一相同就沿用上一份字串。多條執行緒共用：每格是不可變物件、整格換掉，
        // 讀到哪一份都先比完整位元組才採用，互蓋只影響命中率。
        protected unsafe string GetStringFromBytesCached(byte* source, int size)
        {
            var bytes = new ReadOnlySpan<byte>(source, size);
            var hash = new HashCode();
            hash.AddBytes(bytes);
            int set = (hash.ToHashCode() & (NameCacheSets - 1)) * 2;

            var first = Volatile.Read(ref nameCache[set]);
            if (first != null && bytes.SequenceEqual(first.Key))
                return first.Value;
            var second = Volatile.Read(ref nameCache[set + 1]);
            if (second != null && bytes.SequenceEqual(second.Key))
                return second.Value;

            var value = FFXIVMemory.GetStringFromBytes(source, size);
            Volatile.Write(ref nameCache[set + 1], first);
            Volatile.Write(ref nameCache[set], new NameCacheEntry(bytes.ToArray(), value));
            return value;
        }

        private sealed class NameCacheEntry
        {
            public readonly byte[] Key;
            public readonly string Value;

            public NameCacheEntry(byte[] key, string value)
            {
                Key = key;
                Value = value;
            }
        }

        public void ReturnCombatant(Combatant combatant)
        {
            if (combatant == null)
            {
                return;
            }
            
            combatantPool.Return(combatant);
        }

        // Returns a combatant if the combatant is a mob or a PC.
        protected abstract unsafe Combatant GetMobFromByteArray(byte[] source, uint mycharID);

        // 預設照常建 Effects、不用 recycle（結果不會錯，只是沒省到配置）；要省的版本自己覆寫。
        protected virtual Combatant GetMobFromByteArrayWithoutEffects(byte[] source, uint mycharID, ObjectPool<Combatant> recycle)
        {
            return GetMobFromByteArray(source, mycharID);
        }

        // Will return any kind of combatant, even if not a mob.
        // This function always returns a combatant object, even if empty.
        protected abstract unsafe Combatant GetCombatantFromByteArray(
            byte[] source, uint mycharID, bool isPlayer, bool exceptEffects = false);

        protected unsafe List<EffectEntry> GetEffectEntries(byte* source, ObjectType type, uint mycharID)
        {
            var result = new List<EffectEntry>();
            int maxEffects = (type == ObjectType.PC) ? 30 : 60;
            var size = EffectMemory.Size * maxEffects;

            var bytes = ArrayPool<byte>.Shared.Rent(size);
            Marshal.Copy((IntPtr)source, bytes, 0, size);

            for (int i = 0; i < maxEffects; i++)
            {
                var effect = GetEffectEntryFromByteArray(bytes, i);

                if (effect.BuffID > 0 &&
                    effect.Stack >= 0 &&
                    effect.Timer >= 0.0f &&
                    effect.ActorID > 0)
                {
                    effect.isOwner = effect.ActorID == mycharID;

                    result.Add(effect);
                }
            }

            ArrayPool<byte>.Shared.Return(bytes);
            return result;
        }

        protected unsafe EffectEntry GetEffectEntryFromByteArray(byte[] source, int num = 0)
        {
            fixed (byte* p = source)
            {
                EffectMemory mem = *(EffectMemory*)&p[num * EffectMemory.Size];

                EffectEntry effectEntry = new EffectEntry()
                {
                    BuffID = mem.BuffID,
                    Stack = mem.Stack,
                    Timer = mem.Timer,
                    ActorID = mem.ActorID,
                    isOwner = false,
                };

                return effectEntry;
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = Size)]
        public struct EffectMemory
        {
            public const int Size = 12;

            [FieldOffset(0)]
            public ushort BuffID;

            [FieldOffset(2)]
            public ushort Stack;

            [FieldOffset(4)]
            public float Timer;

            [FieldOffset(8)]
            public uint ActorID;
        }
    }
}
