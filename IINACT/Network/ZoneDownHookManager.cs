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
	
	// null＝封包接收函式沒對上，這次載入沒有掛 hook（收不到網路資料）。
	private readonly Hook<DownPrototype>? zoneDownHook;

    public bool IsHooked => zoneDownHook != null;

    // 0＝還沒收到任何封包；解析器監看用它分辨「遊戲沒送」與「解析器沒吃」。
    private long lastPacketTicks;
    public long LastPacketTicks => Volatile.Read(ref lastPacketTicks);

	private readonly SimpleBuffer buffer;
    
    private readonly VersionConstants versionConstants;
    private readonly IUnscrambler unscrambler;

    // ==== ActionEffect 解密驗證診斷（殘留寫 Warning、正確每次載入報一次 Information、其餘 Debug） ====
    // 未使用的 effect slot 伺服器不會填任何東西（整筆 entry 為 0），所以它的 value 欄位在正確解密後必定是 0；
    // 解密後不是 0，就代表這一層的常數／金鑰對台服是錯的。
    // 位移（以 IPC 起點為 0）照 Unscrambler73.UnscrambleActionEffect 自己動手的位置：actionId 在 +24，
    // effect 值從 +64 起、每筆 8 bytes、每個目標 8 槽。
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
    private bool diagOkReported;

	public ZoneDownHookManager(
        INotificationManager notificationManager,
		IGameInteropProvider hooks)
    {
        this.notificationManager = notificationManager;
		buffer = new SimpleBuffer(1024 * 1024);
        // 掃描器帶一份主模組大小的複本，只在這個建構子裡用；掃出來的位址都指向真的模組，不指向複本。
        using var multiScanner = new MultiSigScanner();
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
            // 台服 = 國際服 7.3 世代演算法（Unscrambler73）；opcode 與取模數逐版本寫在 TcObfuscationTables。
            // 表上沒有的版本一律不解混淆：用錯版本的 opcode 會把別種封包 XOR 壞，比不解更糟。
            var (tcTableOffset, tcTableSize) = TryDiscoverOpcodeKeyTable(multiScanner, moduleBase);
            TcObfuscationTables.TryGetValue(version, out var tcTable);
            if (tcTable == null)
            {
                Plugin.Log.Warning(
                    $"[ZoneDownHookManager] 台服遊戲版本 {version} 不在本版 IINACT 的解混淆表內" +
                    $"（已知：{string.Join("、", TcObfuscationTables.Keys)}）——本次載入完全不做解混淆。" +
                    "Machina 的台服 opcode 表也沒有對這個版本驗證過，戰鬥數值解析大概率不正確，要等 IINACT 更新。");
                SendNotification("Game version ?? is not supported by this IINACT build yet. Combat parsing will be wrong until IINACT is updated.".Loc(version));
                tcTableSize = 0;
            }
            else if (tcTableSize != tcTable.KeyTableEntries * 4)
            {
                Plugin.Log.Warning(
                    $"[ZoneDownHookManager] 台服 {version}：掃到的 opcodeKeyTable 有 {tcTableSize / 4} 筆，" +
                    $"但這個版本執行檔的取模數是 {tcTable.KeyTableEntries}——位址或長度不對，本次載入不做解混淆（fail-closed）。");
                tcTableSize = 0;
            }
            else
            {
                Plugin.Log.Information(
                    $"[ZoneDownHookManager] 台服 {version}：opcodeKeyTable 模組位移 0x{tcTableOffset:X}、" +
                    $"{tcTableSize / 4} 筆 int32（與這個版本的取模數一致）");
            }
            versionConstants = GetTraditionalChineseVersionConstants(version, tcTable, tcTableOffset, tcTableSize);
            unscrambler = new Unscrambler73();
            unscrambler.Initialize(versionConstants);
        }
        else
        {
            Plugin.Log.Warning("[ZoneDownHookManager] Creating fallback Unscrambler constants dynamically");
            var (fallbackTableOffset, fallbackTableSize) = TryDiscoverOpcodeKeyTable(multiScanner, moduleBase);
            versionConstants = GetFallbackVersionConstant(fallbackTableOffset, fallbackTableSize);
            unscrambler = new Unscrambler73();
            unscrambler.Initialize(versionConstants);
        }

        var rawOpcodeKeyTable = new byte[versionConstants.OpcodeKeyTableSize];
        opcodeKeyTable = new int[rawOpcodeKeyTable.Length / 4];
        if (rawOpcodeKeyTable.Length > 0)
        {
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
                $"；每輪最多 {DiagPacketsPerWindow} 筆，金鑰更換或 {DiagWindowMs / 60000} 分鐘後重開一輪" +
                "；🔴 殘留寫 Warning，✅ 解密正確每次載入只報一次 Information，其餘無法判定的留 Debug");

        var zoneDownAddress = TryFindZoneDown(multiScanner, out var zoneDownHits);
        if (zoneDownAddress == nint.Zero)
        {
            var hitsText = zoneDownHits > ZoneDownExpectedHits ? $"{zoneDownHits} 個以上" : $"{zoneDownHits} 個";
            Plugin.Log.Warning(
                $"[ZoneDownHookManager] 封包接收函式的特徵碼對不上（{GenericDownSignature} 要恰好 {ZoneDownExpectedHits} 個命中，實際 {hitsText}）——" +
                "本次載入不掛 hook：收不到網路資料，戰鬥解析停用，要等 IINACT 更新。其餘功能照常載入。");
            SendNotification("IINACT could not find the game's network receive function, so it gets no network data. Combat parsing is disabled until IINACT is updated.".Loc());
            return;
        }

		zoneDownHook = hooks.HookFromAddress<DownPrototype>(zoneDownAddress, ZoneDownDetour);

		Enable();
    }

    private const int ZoneDownExpectedHits = 3;

    // 只有恰好 3 個命中才回第 3 個的位址；其餘回 0，呼叫端不建立 hook。
    // hits＝實際命中數，數到 4 為止（4＝4 個以上）。
    // 刻意不在命中數不對時挑一個來掛：多一個或少一個命中時「第 3 個」是別的函式，掛上去會讀錯記憶體。
    private static nint TryFindZoneDown(MultiSigScanner multiScanner, out int hits)
    {
        var address = nint.Zero;
        for (hits = ZoneDownExpectedHits + 1; hits > 0; hits--)
        {
            try
            {
                var found = multiScanner.ScanText(GenericDownSignature, hits);
                if (hits == ZoneDownExpectedHits)
                    address = found[ZoneDownExpectedHits - 1];
                break;
            }
            catch (KeyNotFoundException)
            {
            }
        }

        return address;
    }
    
    private static bool IsModulePointer(ReadOnlySpan<byte> memory, int offset, nint moduleBase, long moduleSize)
    {
        if (offset + 8 > memory.Length) return false;
        var ptr = BitConverter.ToUInt64(memory.Slice(offset));
        return ptr >= (ulong)moduleBase && ptr < (ulong)(moduleBase + moduleSize);
    }

    /// <summary>
    /// 從 <c>PacketDispatcher::OnReceivePacket</c> 內把 opcodeKeyTable 的模組位移與長度掃出來。
    /// 長度＝從表頭往後找到第一個「看起來是模組指標」的 8 bytes 為止（表本身是 int32 陣列，
    /// 後面緊接著才是指標區）。
    /// </summary>
    /// <returns>掃不到時回 (0, 0)。呼叫端必須把它當成「這一輪不解混淆」——
    /// 不要拿寫死的位址頂上去：改版後寫死的位址只會靜默指到別的資料上，
    /// 那比不解混淆更難查。</returns>
    private static (uint Offset, int Size) TryDiscoverOpcodeKeyTable(MultiSigScanner multiScanner, nint moduleBase)
    {
        try
        {
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
            if (opcodeKeyTableSize >= 4 &&
                memory[opcodeKeyTableSize - 1] == 0 && memory[opcodeKeyTableSize - 2] == 0 &&
                memory[opcodeKeyTableSize - 3] == 0 && memory[opcodeKeyTableSize - 4] == 0)
            {
                Plugin.Log.Debug("Uneven padded length for opcode key table");
                opcodeKeyTableSize -= 4;
            }
            Plugin.Log.Debug(
                $"[ZoneDownHookManager] opcodeKeyTableOffset {opcodeKeyTableOffset:X}, opcodeKeyTableSize {opcodeKeyTableSize:X}");
            if (opcodeKeyTableSize == 0)
                throw new Exception("Opcode key table is empty");
            return (opcodeKeyTableOffset, opcodeKeyTableSize);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "[ZoneDownHookManager] opcodeKeyTable 探測失敗（解混淆將整個停用）");
            return (0, 0);
        }
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
                // diagOkReported 刻意不在這裡重置：✅ 每次載入只報一次，照輪報等於每次換區都寫一對 Information。
                Plugin.Log.Debug($"[UpdateKeys] keys {dispatcher->Key0}, {dispatcher->Key1}, {dispatcher->Key2}");
                Plugin.Log.Debug($"[UpdateKeys] game random {dispatcher->GameRandom}, packet random {dispatcher->LastPacketRandom}");
                // ⚠️ 不要在這裡把 key0/1/2 寫進 opcodeKeyTable：那是執行檔 .rdata 裡的靜態表，跟這三把 session 金鑰無關；
                //    兩者在 (opcode + baseKey) % 表長 的索引式裡各司其職。
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
        // 刻意不在這裡檢查 opcodeKeyTable 前幾格是不是 0：那是靜態表（表值本來就不是 0），
        // 金鑰本身在 PacketsFromFrame 每碰到一筆混淆封包都會 UpdateKeys()。
	    var ret = zoneDownHook!.OriginalDisposeSafe(data, a2, a3, a4, a5);

	    var packetOffset = *(uint*)(data + 28);
	    if (packetOffset != 0) return ret;
	    Volatile.Write(ref lastPacketTicks, Environment.TickCount64);
	    
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

            var summary =
                $"[效果解密診斷] {name} opcode=0x{opcode:X4} 封包長度={before.Length} 掃描槽數={slots}" +
                (truncated ? $"（封包比預期的 {expectedSlots} 槽短，已截斷）" : string.Empty) +
                $" 標頭effectCount={effectCount} actionId {actionId} keys={keys[0]}/{keys[1]}/{keys[2]}";
            var detailLine =
                $"[效果解密診斷] 前 {Math.Min(slots, DiagMaxDetailSlots)} 槽 " +
                (detail.Length > 0 ? detail.ToString() : "（無可掃描的槽，封包過短）");

            // residue＝解密表對不上台服，是唯一該吵醒使用者的結果；ok＝金絲雀成立。
            string verdict;
            var residue = false;
            var ok = false;

            if (slots == 0)
                verdict = "[效果解密診斷] 判定：封包過短、掃不到 effect 陣列，本筆無法判定";
            else if (changed == 0)
                verdict = "[效果解密診斷] 判定：解密前後完全一致＝這一筆根本沒被解密" +
                          "（金鑰全 0，或這個 opcode 沒進解密分支），本筆無法判定";
            else if (empty == 0)
                verdict = $"[效果解密診斷] 判定：本筆沒有未用槽（{used} 槽全都有內容），本筆無法判定；" +
                          $"實測XOR=0x{observedXor:X4}";
            else if (emptyNonZeroAfter == 0)
            {
                ok = true;
                verdict = $"[效果解密診斷] 判定：未用槽 {empty} 個解密後全為 0 ✅ 解密正確" +
                          $"（實測XOR=0x{observedXor:X4}）。這就是修好之後該出現的那一行；" +
                          "哪天改版又變成 🔴 殘留，代表 opcodeKeyTable 位址或金鑰推導又對不上了。";
            }
            else if (emptyZeroBefore == empty)
            {
                residue = true;
                verdict = $"[效果解密診斷] 判定：未用槽 {empty} 個解密「前」全是 0，解密「後」有 {emptyNonZeroAfter} 個變成非 0 " +
                          $"🔴 殘留（多解一次）。台服這個欄位根本沒被混淆，我們卻照樣 XOR＝" +
                          $"自己製造出 0x{observedXor:X4} 這個假傷害值。";
            }
            else
            {
                residue = true;
                verdict = $"[效果解密診斷] 判定：未用槽 {empty} 個之中有 {emptyNonZeroAfter} 個解密後非零 🔴 殘留。" +
                          $"混淆確實存在但常數／金鑰對不上台服（實測XOR=0x{observedXor:X4}）＝解密表過期確診。";
            }

            if (residue)
            {
                Plugin.Log.Warning(summary);
                Plugin.Log.Warning(detailLine);
                Plugin.Log.Warning(verdict);
            }
            else if (ok)
            {
                // 每次載入只報一次，但抽樣不停——之後任何一輪翻成 🔴 仍然吵得出來。
                if (diagOkReported) return;
                diagOkReported = true;
                Plugin.Log.Information(summary);
                Plugin.Log.Information(verdict);
            }
            else
            {
                Plugin.Log.Debug(summary);
                Plugin.Log.Debug(detailLine);
                Plugin.Log.Debug(verdict);
            }
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

    private sealed record TcObfuscationTable(
        int InitZone, int UnknownObfuscationInit, int ObfuscationEnabledMode, int KeyTableEntries,
        IReadOnlyDictionary<string, int> Opcodes);

    /// <summary>
    /// 台服各遊戲版本（ffxivgame.ver 全字串）的解混淆 opcode 與 opcodeKeyTable 取模數，
    /// 全部取自該版執行檔的 <c>PacketDispatcher::OnReceivePacket</c>（推導工具 iinact_tc_opcodes_derive.py）。
    /// 改版後新增一筆；舊版本的項目不必保留。
    /// </summary>
    private static readonly Dictionary<string, TcObfuscationTable> TcObfuscationTables = new()
    {
        ["2026.09.14.0000.0000"] = new TcObfuscationTable(
            InitZone: 0x0EB, UnknownObfuscationInit: 0x345, ObfuscationEnabledMode: 0x97, KeyTableEntries: 233,
            Opcodes: new Dictionary<string, int>
            {
                { "PlayerSpawn", 0x37C }, { "NpcSpawn", 0x374 }, { "NpcSpawn2", 0x2D6 },
                { "ActionEffect01", 0x1BE }, { "ActionEffect02", 0x336 }, { "ActionEffect04", 0x335 },
                { "ActionEffect08", 0x13C }, { "ActionEffect16", 0x133 }, { "ActionEffect24", 0x07A },
                { "ActionEffect32", 0x1DF }, { "StatusEffectList", 0x300 }, { "StatusEffectList3", 0x16E },
                { "Examine", 0x1E1 }, { "UpdateGearset", 0x140 }, { "UpdateParty", 0x30C },
                { "ActorControl", 0x325 }, { "ActorCast", 0x35D },
                { "UnknownEffect01", 0x365 }, { "UnknownEffect16", 0x0C3 },
            }),
    };

    private static readonly (string Key, string? MachinaKey)[] TcObfuscatedKeys =
    {
        ("PlayerSpawn", "PlayerSpawn"), ("NpcSpawn", "NpcSpawn"), ("NpcSpawn2", "NpcSpawn2"),
        ("ActionEffect01", "Ability1"), ("ActionEffect02", null), ("ActionEffect04", null),
        ("ActionEffect08", "Ability8"), ("ActionEffect16", "Ability16"), ("ActionEffect24", "Ability24"),
        ("ActionEffect32", "Ability32"), ("StatusEffectList", "StatusEffectList"), ("StatusEffectList3", "StatusEffectList3"),
        ("Examine", null), ("UpdateGearset", null), ("UpdateParty", null),
        ("ActorControl", "ActorControl"), ("ActorCast", "ActorCast"),
        ("UnknownEffect01", null), ("UnknownEffect16", null),
    };

    /// <summary>
    /// <paramref name="table"/> 為 null（未知版本）時所有 opcode 都填 0：0 不是任何區域封包的 opcode，
    /// 等於整個停用解混淆。Machina 表與執行檔推導值不同時以推導值為準，並寫 Warning（Machina 的表過期）。
    /// </summary>
    private static VersionConstants GetTraditionalChineseVersionConstants(string version, TcObfuscationTable? table,
                                                                          uint opcodeKeyTableOffset, int opcodeKeyTableSize)
    {
        var machina = Machina.FFXIV.Headers.Opcodes.OpcodeManager.Instance.CurrentOpcodes;
        var obfuscated = new Dictionary<string, int>();
        foreach (var (key, machinaKey) in TcObfuscatedKeys)
        {
            var value = table?.Opcodes[key] ?? 0;
            if (table != null && machinaKey != null && machina.TryGetValue(machinaKey, out var m) && m != value)
                Plugin.Log.Warning(
                    $"[ZoneDownHookManager] opcode {machinaKey}：Machina 台服表寫 0x{m:X}、{version} 執行檔推導是 0x{value:X}——" +
                    "解混淆以推導值為準；Machina 的表過期，戰鬥數值解析會不正確（要更新 machina 子模組的 TraditionalChinese.txt）。");
            obfuscated[key] = value;
        }

        return new VersionConstants
        {
            GameVersion = version,
            // 這三個欄位 IINACT 與 Unscrambler73 都不讀，只當文件用。
            InitZoneOpcode = table?.InitZone ?? 0,
            UnknownObfuscationInitOpcode = table?.UnknownObfuscationInit ?? 0,
            ObfuscationEnabledMode = (byte)(table?.ObfuscationEnabledMode ?? 0),
            OpcodeKeyTableOffset = opcodeKeyTableOffset,
            OpcodeKeyTableSize = opcodeKeyTableSize,
            ObfuscatedOpcodes = obfuscated
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
