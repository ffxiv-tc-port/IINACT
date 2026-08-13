using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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

    // ==== ActionEffect 解密驗證診斷（Information 級，供使用者回報） ====
    // 目的：判定台服 ActionEffect 的傷害數值欄位在「本層解密之後」是否還留著殘值。
    // 依據：未使用的 effect slot 伺服器不會填任何東西（整筆 entry 為 0），所以它的 value
    //       欄位在正確解密後必定是 0；解密後不是 0，就代表這一層的常數／金鑰對台服是錯的。
    // 位移來源＝Unscrambler72.UnscrambleActionEffect 自己動手的位置（以 IPC 起點為 0）：
    //       actionId  : *(int*)(data + 24)         -= baseKey
    //       effect 值 : *(short*)(data + 64 + i*8) ^= (short)(baseKey + 每 opcode 常數)
    // 交叉對照 Machina Server_ActionEffect1（TraditionalChinese）：EffectEntry 陣列起點在
    //       IPC+58、每筆 8 bytes、value(UInt16) 在 entry+6 → 58+6 = 64，與上面完全吻合。
    private const int DiagPacketsPerWindow = 3;
    private const long DiagWindowMs = 5 * 60 * 1000;
    private const int DiagActionIdOffset = 24;
    private const int DiagEffectValueOffset = 64;
    private const int DiagEffectEntryStride = 8;
    private const int DiagEffectValueInEntry = 6;
    private const int DiagEffectsStart = DiagEffectValueOffset - DiagEffectValueInEntry;
    private const int DiagEffectCountOffset = 49; // Server_ActionEffectHeader.effectCount（IPC 起點為 0）
    private const int DiagMaxDetailSlots = 8;

    private readonly (ushort Opcode, string Name, int TargetCount)[] actionEffectDiagOpcodes;
    private int diagBudget = DiagPacketsPerWindow;
    private long diagExhaustedAt;

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

        actionEffectDiagOpcodes = BuildActionEffectDiagOpcodes(versionConstants);
        if (actionEffectDiagOpcodes.Length == 0)
            Plugin.Log.Information(
                "[效果解密診斷] 沒有任何可監看的 ActionEffect opcode（opcode 表全回 0）——不會有診斷輸出");
        else
            Plugin.Log.Information(
                "[效果解密診斷] 監看中：" +
                string.Join("、", actionEffectDiagOpcodes.Select(x => $"{x.Name}=0x{x.Opcode:X4}")) +
                $"；每輪最多 {DiagPacketsPerWindow} 筆，金鑰更換或 {DiagWindowMs / 60000} 分鐘後重開一輪");

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
                // 金鑰換新（多半是換場景或重連）＝重開一輪 ActionEffect 解密診斷預算
                diagBudget = DiagPacketsPerWindow;
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

                // pktData 仍是解密「前」的原始位元組（Unscramble 只寫 buffer 裡的那份副本），
                // 所以這裡可以直接拿同一筆封包做前後對照，不需要另外複製一份。
                if (TryTakeActionEffectDiagnosticSlot(pktOpcode, out var diagName, out var diagTargets))
                    LogActionEffectDiagnostic(diagName, pktOpcode, diagTargets, pktData, slice);
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
    
    private static (ushort Opcode, string Name, int TargetCount)[] BuildActionEffectDiagOpcodes(
        VersionConstants constants)
    {
        var candidates = new (string Key, int TargetCount)[]
        {
            ("ActionEffect01", 1), ("ActionEffect02", 2), ("ActionEffect04", 4), ("ActionEffect08", 8),
            ("ActionEffect16", 16), ("ActionEffect24", 24), ("ActionEffect32", 32)
        };

        var result = new List<(ushort, string, int)>(candidates.Length);
        foreach (var (key, targetCount) in candidates)
        {
            if (!constants.ObfuscatedOpcodes.TryGetValue(key, out var opcode)) continue;
            // 0 代表台服 opcode 表沒有這一項；拿 0 去比對會誤命中一大堆無關封包
            if (opcode is <= 0 or > ushort.MaxValue) continue;
            result.Add(((ushort)opcode, key, targetCount));
        }

        return result.ToArray();
    }

    /// <summary>
    /// 熱路徑閘門。預算用完之後，每筆封包只多付一次 int 比較（外加一次 tick 比較）：
    /// 不是 ActionEffect 就直接回 false，不組字串、不掃封包內容。
    /// </summary>
    private bool TryTakeActionEffectDiagnosticSlot(ushort opcode, out string name, out int targetCount)
    {
        name = string.Empty;
        targetCount = 0;

        if (diagBudget <= 0)
        {
            if (Environment.TickCount64 - diagExhaustedAt < DiagWindowMs) return false;
            diagBudget = DiagPacketsPerWindow;
        }

        var table = actionEffectDiagOpcodes;
        for (var i = 0; i < table.Length; i++)
        {
            if (table[i].Opcode != opcode) continue;
            name = table[i].Name;
            targetCount = table[i].TargetCount;
            if (--diagBudget <= 0) diagExhaustedAt = Environment.TickCount64;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 把同一筆 ActionEffect 封包的「解密前 vs 解密後」effect 陣列摘要寫進 log。
    /// 只讀不寫，而且自己吞掉所有例外——診斷絕不能影響封包解析。
    /// </summary>
    private void LogActionEffectDiagnostic(string name, ushort opcode, int targetCount,
                                           ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        try
        {
            var len = Math.Min(before.Length, after.Length);

            var slots = Math.Max((len - DiagEffectsStart) / DiagEffectEntryStride, 0);
            var expectedSlots = 8 * targetCount;
            var truncated = slots < expectedSlots;
            if (slots > expectedSlots) slots = expectedSlots;

            var actionId = len >= DiagActionIdOffset + 4
                               ? $"0x{BitConverter.ToUInt32(before.Slice(DiagActionIdOffset, 4)):X8}" +
                                 $"->0x{BitConverter.ToUInt32(after.Slice(DiagActionIdOffset, 4)):X8}"
                               : "(封包過短)";
            var effectCount = len > DiagEffectCountOffset ? before[DiagEffectCountOffset].ToString() : "?";

            int empty = 0, emptyNonZeroAfter = 0, emptyZeroBefore = 0, used = 0, changed = 0;
            var observedXor = 0;
            var detail = new StringBuilder(320);

            for (var i = 0; i < slots; i++)
            {
                var entry = DiagEffectsStart + i * DiagEffectEntryStride;
                var valueBefore = BitConverter.ToUInt16(before.Slice(entry + DiagEffectValueInEntry, 2));
                var valueAfter = BitConverter.ToUInt16(after.Slice(entry + DiagEffectValueInEntry, 2));

                // 「未使用」用整筆 entry 的前 6 bytes 全 0 判定，比只看單一欄位更不怕欄位對錯位。
                var isEmpty = true;
                for (var j = 0; j < DiagEffectValueInEntry; j++)
                {
                    if (before[entry + j] == 0) continue;
                    isEmpty = false;
                    break;
                }

                if (valueBefore != valueAfter)
                {
                    changed++;
                    observedXor = valueBefore ^ valueAfter;
                }

                if (isEmpty)
                {
                    empty++;
                    if (valueBefore == 0) emptyZeroBefore++;
                    if (valueAfter != 0) emptyNonZeroAfter++;
                }
                else
                {
                    used++;
                }

                if (i >= DiagMaxDetailSlots) continue;
                if (detail.Length > 0) detail.Append(" | ");
                detail.Append('[').Append(i).Append(']')
                      .Append(isEmpty ? "未用 " : "已用 ")
                      .Append("型=").Append(before[entry].ToString("X2"))
                      .Append(" 旗標=").Append(before[entry + 5].ToString("X2"))
                      .Append(" 值 ").Append(valueBefore.ToString("X4"))
                      .Append(" -> ").Append(valueAfter.ToString("X4"));
            }

            Plugin.Log.Information(
                $"[效果解密診斷] {name} opcode=0x{opcode:X4} 封包長度={before.Length} 掃描槽數={slots}" +
                (truncated ? $"（封包比預期的 {expectedSlots} 槽短，已截斷）" : string.Empty) +
                $" 標頭effectCount={effectCount} actionId {actionId} keys={keys[0]}/{keys[1]}/{keys[2]}");
            Plugin.Log.Information(
                $"[效果解密診斷] 前 {Math.Min(slots, DiagMaxDetailSlots)} 槽 " +
                (detail.Length > 0 ? detail.ToString() : "（無可掃描的槽，封包過短）"));

            if (slots == 0)
                Plugin.Log.Information(
                    "[效果解密診斷] 判定：封包過短、掃不到 effect 陣列，本筆無法判定");
            else if (changed == 0)
                Plugin.Log.Information(
                    "[效果解密診斷] 判定：解密前後完全一致＝這一筆根本沒被解密" +
                    "（金鑰全 0，或這個 opcode 沒進解密分支），本筆無法判定");
            else if (empty == 0)
                Plugin.Log.Information(
                    $"[效果解密診斷] 判定：本筆沒有未用槽（{used} 槽全都有內容），本筆無法判定；" +
                    $"實測XOR=0x{observedXor:X4}");
            else if (emptyNonZeroAfter == 0)
                Plugin.Log.Information(
                    $"[效果解密診斷] 判定：未用槽 {empty} 個解密後全為 0 ✅ 正常。" +
                    $"殘值不在解密這一層 → 方向改追 FFXIV_ACT_Plugin 內部累加器。實測XOR=0x{observedXor:X4}");
            else if (emptyZeroBefore == empty)
                Plugin.Log.Information(
                    $"[效果解密診斷] 判定：未用槽 {empty} 個解密「前」全是 0，解密「後」有 {emptyNonZeroAfter} 個變成非 0 " +
                    $"🔴 殘留（多解一次）。台服這個欄位根本沒被混淆，我們卻照樣 XOR＝" +
                    $"自己製造出 0x{observedXor:X4} 這個假傷害值。");
            else
                Plugin.Log.Information(
                    $"[效果解密診斷] 判定：未用槽 {empty} 個之中有 {emptyNonZeroAfter} 個解密後非零 🔴 殘留。" +
                    $"混淆確實存在但常數／金鑰對不上台服（實測XOR=0x{observedXor:X4}）＝解密表過期確診。");
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "[效果解密診斷] 診斷本身出錯（已吞掉，不影響封包解析）");
        }
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
