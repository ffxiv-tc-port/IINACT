using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Unscrambler;
using Unscrambler.Constants;
using Unscrambler.Unscramble;
using Unscrambler.Unscramble.Versions;

namespace IINACT.Network;

public unsafe class ZoneDownHookManager : IDisposable
{
	private const string GenericDownSignature = "E8 ?? ?? ?? ?? 4C 8B 4F 10 8B 47 1C 45";
    private const string OpcodeKeyTableSignature = "?? ?? ?? 2B C8 ?? 8B ?? 8A ?? ?? ?? ?? 41 81";
    private readonly int[] opcodeKeyTable;
    private readonly byte[] keys = new byte[3];
    private readonly bool isTraditionalChinese;
    
    private readonly INotificationManager notificationManager;
	private delegate nuint DownPrototype(byte* data, byte* a2, nuint a3, nuint a4, nuint a5);
	
	private readonly Hook<DownPrototype> zoneDownHook;
    
	private readonly SimpleBuffer buffer;
    
    private readonly VersionConstants versionConstants;
    private readonly IUnscrambler unscrambler;

	public ZoneDownHookManager(
        INotificationManager notificationManager,
		IGameInteropProvider hooks)
    {
        this.notificationManager = notificationManager;
		buffer = new SimpleBuffer(1024 * 1024);
        var multiScanner = new MultiSigScanner();
        var moduleBase = multiScanner.Module.BaseAddress;
        
        var version = GetRunningGameVersion();
        var gameRegion = Machina.FFXIV.Headers.Opcodes.OpcodeManager.Instance.GameRegion;
        var isGlobal = gameRegion == Machina.FFXIV.GameRegion.Global;
        isTraditionalChinese = gameRegion == Machina.FFXIV.GameRegion.TraditionalChinese;

        if (isGlobal && VersionConstants.Constants.ContainsKey(version))
        {
            versionConstants = VersionConstants.ForGameVersion(version);
            unscrambler = UnscramblerFactory.ForGameVersion(version);
        }
        else if (isTraditionalChinese)
        {
            // TC: key table is Key0/Key1/Key2 in PacketDispatcher (3 × int32 = 12 bytes).
            // There is no static module-relative table; keys are updated dynamically each session.
            Plugin.Log.Warning("[ZoneDownHookManager] TraditionalChinese region: using dynamic 3-entry key table from PacketDispatcher");
            versionConstants = GetTraditionalChineseVersionConstants();
            unscrambler = new Unscrambler72();
            unscrambler.Initialize(versionConstants);
        }
        else
        {
            Plugin.Log.Warning("[ZoneDownHookManager] Creating fallback Unscrambler constants dynamically");
            var onReceivePacketAddress = PacketDispatcher.GetOnReceivePacketAddress();
            Plugin.Log.Debug($"[ZoneDownHookManager] GetOnReceivePacketAddress: {onReceivePacketAddress:X}");
            var opcodeKeyTableIns = MultiSigScanner.Scan(onReceivePacketAddress, 0x1000, OpcodeKeyTableSignature);
            var bytes = new byte[13];
            Marshal.Copy(opcodeKeyTableIns, bytes, 0, 13);
            var opcodeKeyTableOffset = BitConverter.ToUInt32(bytes, 9);
            var opcodeKeyTableAddress = moduleBase + (nint)opcodeKeyTableOffset;
            var searchRange = 0x1000;
            var memory = new byte[searchRange];
            Marshal.Copy(opcodeKeyTableAddress, memory, 0, searchRange);
            var moduleSize = multiScanner.Module.ModuleMemorySize;
            var opcodeKeyTableSize = 0;
            while (!IsModulePointer(memory, opcodeKeyTableSize, moduleBase, moduleSize))
            {
                opcodeKeyTableSize += 4;
                if (opcodeKeyTableSize > searchRange)
                    throw new Exception("Opcode key table size is too large");
            }
            if (memory[opcodeKeyTableSize - 1] == 0 && memory[opcodeKeyTableSize - 2] == 0 && memory[opcodeKeyTableSize - 3] == 0 && memory[opcodeKeyTableSize - 4] == 0)
            {
                Plugin.Log.Debug("Uneven padded length for opcode key table");
                opcodeKeyTableSize -= 4;
            }
            Plugin.Log.Debug(
                $"[ZoneDownHookManager] opcodeKeyTableOffset {opcodeKeyTableOffset:X}, opcodeKeyTableSize {opcodeKeyTableSize:X}");
            versionConstants = GetFallbackVersionConstant(opcodeKeyTableOffset, opcodeKeyTableSize);
            unscrambler = new Unscrambler73();
            unscrambler.Initialize(versionConstants);
        }

        if (isTraditionalChinese)
        {
            // Initialized to zero; populated by UpdateKeys() once the dispatcher has valid keys.
            opcodeKeyTable = new int[3];
            Plugin.Log.Debug("[ZoneDownHookManager] TC: opcodeKeyTable will be populated from PacketDispatcher keys");
        }
        else
        {
            var rawOpcodeKeyTable = new byte[versionConstants.OpcodeKeyTableSize];
            opcodeKeyTable = new int[rawOpcodeKeyTable.Length / 4];
            Marshal.Copy(moduleBase + (nint)versionConstants.OpcodeKeyTableOffset, rawOpcodeKeyTable, 0, rawOpcodeKeyTable.Length);
            Plugin.Log.Debug("[ZoneDownHookManager] raw opcode key table {@Data} (length: {Length})", rawOpcodeKeyTable, rawOpcodeKeyTable.Length);
            for (var i = 0; i < rawOpcodeKeyTable.Length; i += 4)
                opcodeKeyTable[i / 4] = BitConverter.ToInt32(rawOpcodeKeyTable, i);
        }

        var rxPtrs = multiScanner.ScanText(GenericDownSignature, 3);
		zoneDownHook = hooks.HookFromAddress<DownPrototype>(rxPtrs[2], ZoneDownDetour);

		Enable();
    }
    
    private static bool IsModulePointer(ReadOnlySpan<byte> memory, int offset, nint moduleBase, long moduleSize)
    {
        if (offset + 8 > memory.Length) return false;
        var ptr = BitConverter.ToUInt64(memory.Slice(offset));
        return ptr >= (ulong)moduleBase && ptr < (ulong)(moduleBase + moduleSize);
    }

	public void Enable()
    {
        UpdateKeys();
		zoneDownHook?.Enable();
	}
    
    private void UpdateKeys()
    {
        var dispatcher = PacketDispatcher.GetInstance();
        
        if (dispatcher != null)
        {
            var gameRandom = dispatcher->GameRandom;
            var packetRandom = dispatcher->LastPacketRandom;
            byte key0 = 0, key1 = 0, key2 = 0;
            
            var obfuscationKeysLoaded = dispatcher->Key0 >= gameRandom + packetRandom;

            if (obfuscationKeysLoaded)
            {
                key0 = (byte)(dispatcher->Key0 - gameRandom - packetRandom);
                key1 = (byte)(dispatcher->Key1 - gameRandom - packetRandom);
                key2 = (byte)(dispatcher->Key2 - gameRandom - packetRandom);	
            }
            
            if (key0 != keys[0] || key1 != keys[1] || key2 != keys[2])
            {
                keys[0] = key0;
                keys[1] = key1;
                keys[2] = key2;    
                Plugin.Log.Debug($"[UpdateKeys] keys {dispatcher->Key0}, {dispatcher->Key1}, {dispatcher->Key2}");
                Plugin.Log.Debug($"[UpdateKeys] game random {dispatcher->GameRandom}, packet random {dispatcher->LastPacketRandom}");
                if (isTraditionalChinese)
                {
                    // TC uses Key0/Key1/Key2 directly as the 3-entry key table (opcode % 3 indexing).
                    opcodeKeyTable[0] = key0;
                    opcodeKeyTable[1] = key1;
                    opcodeKeyTable[2] = key2;
                }
            }
        }
        else
        {
            Plugin.Log.Warning("[UpdateKeys] Dispatcher was null, so not initializing keys");
        }
    }
	
	public void Disable()
	{
		zoneDownHook?.Disable();
	}
	
	public void Dispose()
	{
		Disable();
		zoneDownHook?.Dispose();
	}
    
    private void SendNotification(string content)
    {
        notificationManager.AddNotification(new Notification
        {
            Content = content,
            Title = "IINACT", 
        });
        Plugin.Log.Debug($"[SendNotification] {content}");
    }
    
    private nuint ZoneDownDetour(byte* data, byte* a2, nuint a3, nuint a4, nuint a5)
    {
        if (isTraditionalChinese && opcodeKeyTable != null && opcodeKeyTable[0] == 0 && opcodeKeyTable[1] == 0 && opcodeKeyTable[2] == 0)
        {
            UpdateKeys();
        }

	    var ret = zoneDownHook.OriginalDisposeSafe(data, a2, a3, a4, a5);

	    var packetOffset = *(uint*)(data + 28);
	    if (packetOffset != 0) return ret;
	    
	    try
	    {
		    PacketsFromFrame((byte*) *(nint*)(data + 16));
	    }
	    catch (Exception e)
	    {
            Plugin.Log.Error(e, "[PacketsFromFrame] Error!");
	    }

        return ret;
    }
    
    private void PacketsFromFrame(byte* framePtr)
    {
        if ((nuint)framePtr == 0)
        {
            Plugin.Log.Error("null ptr");
            return;
        }
        
        var headerSize = Unsafe.SizeOf<FrameHeader>();
        var headerSpan = new Span<byte>(framePtr, headerSize);
        var header = headerSpan.Cast<byte, FrameHeader>();
        var span = new Span<byte>(framePtr, (int)header.TotalSize);
        var data = span.Slice(headerSize, (int)header.TotalSize - headerSize);
        
        // Compression
        if (header.Compression != CompressionType.None)
        {
            SendNotification($"A frame was compressed.");
            return;
        }
        
        GameServerTime.SetLastServerTimestamp(header.TimeValue);
        
        // Deobfuscation
        var offset = 0;
        for (var i = 0; i < header.Count; i++)
        {
	        var pktHdrSize = Unsafe.SizeOf<PacketElementHeader>();
            var pktHdrSlice = data.Slice(offset, pktHdrSize);
            var pktHdr = pktHdrSlice.Cast<byte, PacketElementHeader>();
            var pktData = data.Slice(offset + pktHdrSize, (int)pktHdr.Size - pktHdrSize);
            var pktOpcode = OpcodeUtility.GetOpcodeFromPacketAtIpcStart(pktData);
            var needsDeobfuscation = versionConstants.ObfuscatedOpcodes.ContainsValue(pktOpcode);
            
            buffer.Clear();
            buffer.Write(pktHdrSlice);

            if (needsDeobfuscation)
            {
                UpdateKeys();
                var pos = buffer.Size;
                buffer.Write(pktData);
                var slice = buffer.Get(pos, pktData.Length);
                unscrambler.Unscramble(slice, keys[0], keys[1], keys[2], opcodeKeyTable);
            }
            else
            {
                buffer.Write(pktData);    
            }
            
            EnqueueToMachina(buffer.GetBuffer());
            
            offset += (int)pktHdr.Size;
        }
    }

    private static void EnqueueToMachina(ReadOnlySpan<byte> data)
    {
        var queue = Machina.FFXIV.Dalamud.DalamudClient.MessageQueue;
        queue?.Enqueue((GameServerTime.LastSeverTimestamp, data.ToArray()));
    }
    
    private static string GetRunningGameVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return "0000.00.00.0000.0000";
            var parent = Directory.GetParent(path)?.FullName;
            if (string.IsNullOrEmpty(parent)) return "0000.00.00.0000.0000";
            var ffxivVerFile = Path.Combine(parent, "ffxivgame.ver");
            if (!File.Exists(ffxivVerFile)) return "0000.00.00.0000.0000";
            using var fs = new FileStream(ffxivVerFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd().Trim();
        }
        catch
        {
            return "0000.00.00.0000.0000";
        }
    }

    private static VersionConstants GetTraditionalChineseVersionConstants()
    {
        var opcodes = Machina.FFXIV.Headers.Opcodes.OpcodeManager.Instance.CurrentOpcodes;
        int GetOpcode(string key) => opcodes.TryGetValue(key, out var value) ? value : 0;

        return new VersionConstants
        {
            GameVersion = GetRunningGameVersion(),
            InitZoneOpcode = 0x227,
            UnknownObfuscationInitOpcode = 0x0,
            OpcodeKeyTableOffset = 0,
            OpcodeKeyTableSize = 0,
            TableOffsets = new[] { 0x2162570L, 0x21755F0L, 0x2179560L },
            TableRadixes = new[] { 0xCB, 0x29, 0xE9 },
            TableSizes = new[] { 96 * 0xCB, 99 * 0x29, 128 * 0xE9 },
            MidTableOffset = 0x2162350,
            MidTableSize = 0x44 * 8,
            DayTableOffset = 0x2196760,
            DayTableSize = (0xE + 1) * 4,
            ObfuscatedOpcodes = new Dictionary<string, int>
            {
                { "PlayerSpawn", GetOpcode("PlayerSpawn") },
                { "NpcSpawn", GetOpcode("NpcSpawn") },
                { "NpcSpawn2", GetOpcode("NpcSpawn2") },

                { "ActionEffect01", GetOpcode("Ability1") },
                { "ActionEffect08", GetOpcode("Ability8") },
                { "ActionEffect16", GetOpcode("Ability16") },
                { "ActionEffect24", GetOpcode("Ability24") },
                { "ActionEffect32", GetOpcode("Ability32") },
                { "StatusEffectList", GetOpcode("StatusEffectList") },
                { "StatusEffectList3", GetOpcode("StatusEffectList3") },

                { "Examine", GetOpcode("Examine") },
                { "UpdateGearset", GetOpcode("UpdateGearset") },
                { "UpdateParty", GetOpcode("UpdateParty") },
                { "ActorControl", GetOpcode("ActorControl") },
                { "ActorCast", GetOpcode("ActorCast") },
                { "ActorControlSelf", GetOpcode("ActorControlSelf") },

                { "UnknownEffect01", 0x0 },
                { "UnknownEffect16", 0x0 },
                { "ActionEffect02", 0x0 },
                { "ActionEffect04", 0x0 }
            }
        };
    }
    
    public static VersionConstants GetFallbackVersionConstant(uint opcodeKeyTableOffset, int opcodeKeyTableSize)
    {
        var opcodes = Machina.FFXIV.Headers.Opcodes.OpcodeManager.Instance.CurrentOpcodes;
        return new VersionConstants
        {
            GameVersion = GetRunningGameVersion(),
            InitZoneOpcode = 0x0,
            UnknownObfuscationInitOpcode = 0x0,
            OpcodeKeyTableOffset = opcodeKeyTableOffset,
            OpcodeKeyTableSize = opcodeKeyTableSize,
            ObfuscatedOpcodes = new Dictionary<string, int>
            {
                { "PlayerSpawn", opcodes["PlayerSpawn"] },
                { "NpcSpawn", opcodes["NpcSpawn"] },
                { "NpcSpawn2", opcodes["NpcSpawn2"] },

                { "ActionEffect01", opcodes["Ability1"] },
                { "ActionEffect08", opcodes["Ability8"] },
                { "ActionEffect16", opcodes["Ability16"] },
                { "ActionEffect24", opcodes["Ability24"] },
                { "ActionEffect32", opcodes["Ability32"] },

                { "StatusEffectList", opcodes["StatusEffectList"] },
                { "StatusEffectList3", opcodes["StatusEffectList3"] },

                { "Examine", 0x0 },
                { "UpdateGearset", 0x0 },
                { "UpdateParty", 0x0 },
                { "ActorControl", opcodes["ActorControl"] },
                { "ActorCast", opcodes["ActorCast"] },

                { "UnknownEffect01", 0x0 },
                { "UnknownEffect16", 0x0 },
                { "ActionEffect02", 0x0 },
                { "ActionEffect04", 0x0 }
            }
        };
    }
}
