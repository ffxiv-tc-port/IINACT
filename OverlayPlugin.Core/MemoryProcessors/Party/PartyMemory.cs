using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFXIV_ACT_Plugin.Memory;

namespace RainbowMage.OverlayPlugin.MemoryProcessors.Party
{
    public class PartyListEntry
    {
        public float x;
        public float y;
        public float z;
        public long contentId;
        public uint objectId;
        public uint currentHP;
        public uint maxHP;
        public ushort currentMP;
        public ushort maxMP;
        public ushort territoryType;
        public ushort homeWorld;
        public string name;
        public byte sex;
        public byte classJob;
        public byte level;
        public byte flags;
    }

    public class PartyListsStruct
    {
        public long partyId;
        public long partyId_2;
        public uint partyLeaderIndex;
        public byte memberCount;
        public byte allianceFlags;

        public uint currentPartyFlags;

        public PartyListEntry[] partyMembers;
        public PartyListEntry[] alliance1Members;
        public PartyListEntry[] alliance2Members;
        public PartyListEntry[] alliance3Members;
        public PartyListEntry[] alliance4Members;
        public PartyListEntry[] alliance5Members;

        // 以下給 GetPartyLists(reuse) 重複利用：非公開欄位不進 JSON。段 0＝本隊、1～5＝聯盟各段，每段 8 格。
        internal const int PoolSections = 6, PoolSlots = 8, NameBytes = 0x40;
        private PartyListEntry[] pool;
        private byte[] poolNameBytes;
        private PartyListEntry[][] partyArrays;
        private PartyListEntry[][] allianceArrays;
        private PartyListEntry[] selfArray;

        internal PartyListEntry[] PartyArray(int length)
        {
            partyArrays ??= new PartyListEntry[PoolSlots + 1][];
            return partyArrays[length] ??= new PartyListEntry[length];
        }

        internal PartyListEntry[] AllianceArray(int section)
        {
            allianceArrays ??= new PartyListEntry[PoolSections][];
            return allianceArrays[section] ??= new PartyListEntry[PoolSlots];
        }

        internal PartyListEntry[] SelfArray() => selfArray ??= new[] { new PartyListEntry() };

        // 名字只在原始位元組變了才重新解碼；同樣的位元組解出同樣的字串。
        internal unsafe PartyListEntry PooledEntry(int section, int slot, byte* name)
        {
            pool ??= new PartyListEntry[PoolSections * PoolSlots];
            poolNameBytes ??= new byte[PoolSections * PoolSlots * NameBytes];
            var index = section * PoolSlots + slot;
            var entry = pool[index] ??= new PartyListEntry();
            var now = new ReadOnlySpan<byte>(name, NameBytes);
            var last = poolNameBytes.AsSpan(index * NameBytes, NameBytes);
            if (entry.name == null || !now.SequenceEqual(last))
            {
                entry.name = FFXIVMemory.GetStringFromBytes(name, NameBytes);
                now.CopyTo(last);
            }
            return entry;
        }
    }

    public abstract class PartyMemory
    {
        protected FFXIVMemory memory;
        protected ILogger logger;

        protected IntPtr partyInstanceAddress = IntPtr.Zero;
        
        protected Func<IntPtr> GetGroupManagerAddress;


        public PartyMemory(TinyIoCContainer container)
        {
            logger = container.Resolve<ILogger>();
            memory = container.Resolve<FFXIVMemory>();
            
            var plugin = FFXIVRepository.GetPluginData();
            var readParty = (ISignatureManager)plugin._iocContainer.GetService(typeof(ISignatureManager));
            var sigType = (SignatureType)0x50;

            GetGroupManagerAddress = () => readParty!.Read(sigType);
        }

        public bool IsValid()
        {
            // The GroupManager addresses are static and never change
            // So we don't need to reset pointers or check for valid pointers
            if (!memory.IsValid())
                return false;

            return true;
        }

        public void ScanPointers()
        {
            if (!memory.IsValid())
                return;

            // These addresses aren't pointers, they're static memory structures. Therefore we don't need to resolve nested pointers.
            long instanceAddress = (long)GetGroupManagerAddress();

            // 每次讀隊伍都會走到這裡，位址沒變（平常的情況）時不配置 fail 清單。
            if (instanceAddress != 0 && instanceAddress == partyInstanceAddress.ToInt64())
                return;

            List<string> fail = new List<string>();

            if (instanceAddress != 0)
            {
                partyInstanceAddress = new IntPtr(instanceAddress);
            }
            else
            {
                partyInstanceAddress = IntPtr.Zero;
                fail.Add(nameof(partyInstanceAddress));
            }

            logger.Log(LogLevel.Debug, "partyInstanceAddress: 0x{0:X}", partyInstanceAddress.ToInt64());

            if (fail.Count == 0)
            {
                logger.Log(LogLevel.Info, $"Found party memory via {GetType().Name}.");
                return;
            }

            // @TODO: Change this from Debug to Error once we're actually using party
            logger.Log(LogLevel.Debug, $"Failed to find party memory via {GetType().Name}: {string.Join(", ", fail)}.");
            return;
        }

        public abstract Version GetVersion();

        public IntPtr GetPointer()
        {
            if (!IsValid())
                return IntPtr.Zero;
            return partyInstanceAddress;
        }
    }
}
