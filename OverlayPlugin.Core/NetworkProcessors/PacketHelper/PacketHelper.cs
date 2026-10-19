using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Machina.FFXIV;

namespace RainbowMage.OverlayPlugin.NetworkProcessors.PacketHelper
{
    class RegionalizedPacketHelper<
        HeaderStruct_Global, PacketStruct_Global,
        HeaderStruct_TC, PacketStruct_TC,
        HeaderStruct_CN, PacketStruct_CN,
        HeaderStruct_KR, PacketStruct_KR>
        where HeaderStruct_Global : unmanaged, IHeaderStruct
        where PacketStruct_Global : unmanaged, IPacketStruct
        where HeaderStruct_TC : unmanaged, IHeaderStruct
        where PacketStruct_TC : unmanaged, IPacketStruct
        where HeaderStruct_CN : unmanaged, IHeaderStruct
        where PacketStruct_CN : unmanaged, IPacketStruct
        where HeaderStruct_KR : unmanaged, IHeaderStruct
        where PacketStruct_KR : unmanaged, IPacketStruct
    {
        public readonly PacketHelper<HeaderStruct_Global, PacketStruct_Global> global;
        public readonly PacketHelper<HeaderStruct_TC, PacketStruct_TC> tc;
        public readonly PacketHelper<HeaderStruct_CN, PacketStruct_CN> cn;
        public readonly PacketHelper<HeaderStruct_KR, PacketStruct_KR> kr;

        public RegionalizedPacketHelper(ushort globalOpcode, ushort tcOpcode, ushort cnOpcode, ushort krOpcode)
        {
            global = new PacketHelper<HeaderStruct_Global, PacketStruct_Global>(globalOpcode);
            tc = new PacketHelper<HeaderStruct_TC, PacketStruct_TC>(tcOpcode);
            cn = new PacketHelper<HeaderStruct_CN, PacketStruct_CN>(cnOpcode);
            kr = new PacketHelper<HeaderStruct_KR, PacketStruct_KR>(krOpcode);
        }

        public IPacketHelper this[GameRegion gameRegion]
        {
            get
            {
                switch (gameRegion)
                {
                    case GameRegion.Global: return global;
                    case GameRegion.TraditionalChinese: return tc;
                    case GameRegion.Chinese: return cn;
                    case GameRegion.Korean: return kr;

                    default: return global;
                }
            }
        }

        public static RegionalizedPacketHelper<HeaderStruct_Global, PacketStruct_Global, HeaderStruct_TC, PacketStruct_TC, HeaderStruct_CN, PacketStruct_CN, HeaderStruct_KR, PacketStruct_KR>
            CreateFromMachina(string opcodeName)
        {
            var opcodes = FFXIVRepository.GetMachinaOpcodes();
            if (opcodes == null)
            {
                return null;
            }

            if (!opcodes.TryGetValue(GameRegion.Global, out var globalOpcodes))
            {
                return null;
            }
            if (!opcodes.TryGetValue(GameRegion.TraditionalChinese, out var tcOpcodes))
            {
                return null;
            }
            if (!opcodes.TryGetValue(GameRegion.Chinese, out var cnOpcodes))
            {
                return null;
            }
            if (!opcodes.TryGetValue(GameRegion.Korean, out var krOpcodes))
            {
                return null;
            }

            if (!globalOpcodes.TryGetValue(opcodeName, out var globalOpcode))
            {
                globalOpcode = 0;
            }
            if (!tcOpcodes.TryGetValue(opcodeName, out var tcOpcode))
            {
                tcOpcode = 0;
            }
            if (!cnOpcodes.TryGetValue(opcodeName, out var cnOpcode))
            {
                cnOpcode = 0;
            }
            if (!krOpcodes.TryGetValue(opcodeName, out var krOpcode))
            {
                krOpcode = 0;
            }

            return new RegionalizedPacketHelper<HeaderStruct_Global, PacketStruct_Global, HeaderStruct_TC, PacketStruct_TC, HeaderStruct_CN, PacketStruct_CN, HeaderStruct_KR, PacketStruct_KR>
                (globalOpcode, tcOpcode, cnOpcode, krOpcode);
        }

        public static RegionalizedPacketHelper<HeaderStruct_Global, PacketStruct_Global, HeaderStruct_TC, PacketStruct_TC, HeaderStruct_CN, PacketStruct_CN, HeaderStruct_KR, PacketStruct_KR>
            CreateFromOpcodeConfig(OverlayPluginLogLineConfig opcodeConfig, string opcodeName)
        {
            var globalOpcodeConfigEntry = opcodeConfig[opcodeName, GameRegion.Global.ToString()];
            var tcOpcodeConfigEntry = opcodeConfig[opcodeName, GameRegion.TraditionalChinese.ToString()];
            var cnOpcodeConfigEntry = opcodeConfig[opcodeName, GameRegion.Chinese.ToString()];
            var krOpcodeConfigEntry = opcodeConfig[opcodeName, GameRegion.Korean.ToString()];

            ushort globalOpcode = (ushort)(globalOpcodeConfigEntry?.opcode ?? 0);
            ushort tcOpcode = (ushort)(tcOpcodeConfigEntry?.opcode ?? 0);
            ushort cnOpcode = (ushort)(cnOpcodeConfigEntry?.opcode ?? 0);
            ushort krOpcode = (ushort)(krOpcodeConfigEntry?.opcode ?? 0);

            return new RegionalizedPacketHelper<HeaderStruct_Global, PacketStruct_Global, HeaderStruct_TC, PacketStruct_TC, HeaderStruct_CN, PacketStruct_CN, HeaderStruct_KR, PacketStruct_KR>
                (globalOpcode, tcOpcode, cnOpcode, krOpcode);
        }
    }

    interface IPacketHelper
    {
        string ToString(long epoch, byte[] message);
    }

    class PacketHelper<HeaderStruct, PacketStruct> : IPacketHelper
        where HeaderStruct : unmanaged, IHeaderStruct
        where PacketStruct : unmanaged, IPacketStruct
    {
        public readonly ushort Opcode;
        public readonly int headerSize;
        public readonly int packetSize;

        public PacketHelper(ushort opcode)
        {
            Opcode = opcode;
            headerSize = Marshal.SizeOf(typeof(HeaderStruct));
            packetSize = Marshal.SizeOf(typeof(PacketStruct));
        }

        /// <summary>
        /// Construct a string representation of a packet from a byte array
        /// </summary>
        /// <param name="epoch">epoch timestamp from FFXIV_ACT_Plugin's NetworkReceivedDelegate</param>
        /// <param name="message">Message byte array received from FFXIV_ACT_Plugin's NetworkReceivedDelegate</param>
        /// <returns>null for invalid packet, otherwise a constructed packet</returns>
        public string ToString(long epoch, byte[] message)
        {
            if (ToStructs(message, out var header, out var packet) == false)
            {
                return null;
            }

            return ToString(epoch, header, packet);
        }

        /// <summary>
        /// Construct a string representation of a packet from a byte array
        /// </summary>
        /// <param name="epoch">epoch timestamp from FFXIV_ACT_Plugin's NetworkReceivedDelegate</param>
        /// <param name="message">Message byte array received from FFXIV_ACT_Plugin's NetworkReceivedDelegate</param>
        /// <returns>null for invalid packet, otherwise a constructed packet</returns>
        public string ToString(long epoch, HeaderStruct header, PacketStruct packet)
        {
            return packet.ToString(epoch, header.ActorID);
        }

        public unsafe bool ToStructs(byte[] message, out HeaderStruct header, out PacketStruct packet)
        {
            // Message is too short to contain this packet
            if (message.Length < headerSize + packetSize)
            {
                header = default;
                packet = default;

                return false;
            }


            fixed (byte* messagePtr = message)
            {
                // 契約：兩個結構只能有固定大小的數值欄位（不可有 bool／char，Marshal 會轉換它們），
                // 這時直接讀與 Marshal 逐位元組相同。每個封包都走這裡，Marshal 每次要裝箱一份。
                header = Unsafe.ReadUnaligned<HeaderStruct>(messagePtr);

                if (header.Opcode != Opcode)
                {
                    header = default;
                    packet = default;

                    return false;
                }

                packet = Unsafe.ReadUnaligned<PacketStruct>(messagePtr + headerSize);

                return true;
            }
        }
    }

    interface IPacketStruct
    {
        string ToString(long epoch, uint ActorID);
    }

    interface IHeaderStruct
    {
        uint ActorID { get; }
        uint Opcode { get; }
    }
}
