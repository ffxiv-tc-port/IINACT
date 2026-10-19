using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace FetchDependencies;

internal class Patcher
{
    private Version PluginVersion { get; }
    private string WorkPath { get; }
    private Action<string>? LogInfo { get; }
    private Action<string>? LogWarning { get; }

    /// <summary>Costura 剛解出、還沒經過任何修補的 Resource DLL 的 SHA-256；沒解到或讀不了為 null。</summary>
    private string? ResourceDllSha256 { get; set; }
    private Version? MainPluginVersion { get; set; }

    /// <summary><see cref="MainPlugin"/> 已處理過預篩選時要記的那一行；null＝沒處理過（例如 zip 裡沒有 Resource DLL）。</summary>
    private (Action<string>? Sink, string Message)? PrefilterOutcome { get; set; }

    public Patcher(Version version, string workPath, Action<string>? logInfo = null, Action<string>? logWarning = null)
    {
        PluginVersion = version;
        WorkPath = workPath;
        LogInfo = logInfo;
        LogWarning = logWarning;
    }

    public void MainPlugin()
    {
        var plugin = new TargetAssembly(Path.Combine(WorkPath, "FFXIV_ACT_Plugin.dll"));
        MainPluginVersion = plugin.Version;
        var resources = plugin.Assembly.MainModule.Resources.ToArray();

        foreach (var resource in resources)
        {
            if (Costura.CheckForPlugin(resource.Name))
            {
                using var stream = (resource as EmbeddedResource)!.GetResourceStream();
                var dllPath = Path.Combine(WorkPath, Costura.Fix(resource.Name));
                Costura.Decompress(stream, dllPath);
                if (Path.GetFileName(dllPath) == ResourcePrefilter.ResourceDllName)
                    ResourceDllSha256 = TryHashFile(dllPath);
                var resourceAssembly = new TargetAssembly(dllPath);
                resourceAssembly.RemoveStrongNaming();
                resourceAssembly.MakePublic();
                if (Path.GetFileName(dllPath) == ResourcePrefilter.ResourceDllName)
                    resourceAssembly = PrefilterBeforeWrite(resourceAssembly, dllPath);
                resourceAssembly.WriteOut();
            }

            plugin.Assembly.MainModule.Resources.Remove(resource);
        }

        var method = plugin.GetMethod(
            "System.Void FFXIV_ACT_Plugin.ACTWrapper::RunOnACTUIThread(System.Action)");
        var ilProcessor = method.Body.GetILProcessor();
        ilProcessor.Replace(0, Instruction.Create(OpCodes.Ret));

        plugin.RemoveStrongNaming();
        plugin.MakePublic();
        plugin.WriteOut();
    }

    public void LogFilePlugin()
    {
        var logfile = new TargetAssembly(Path.Combine(WorkPath, "FFXIV_ACT_Plugin.Logfile.dll"));
        {
            var method = logfile.GetMethod(
                "System.Void FFXIV_ACT_Plugin.Logfile.LogOutput::Run(System.Object)");
            var ilProcessor = method.Body.GetILProcessor();
            ilProcessor.Replace(0, Instruction.Create(OpCodes.Ret));
        }

        {
            var method = logfile.GetMethod(
                "System.String FFXIV_ACT_Plugin.Logfile.LogFormat::FormatVersion()");
            var ilProcessor = method.Body.GetILProcessor();
            while (ilProcessor.Body.Instructions.First().OpCode != OpCodes.Ldstr)
                ilProcessor.RemoveAt(0);
            ilProcessor.Replace(
                0, Instruction.Create(OpCodes.Ldstr, $"This is IINACT {PluginVersion} (API {ApiVersion.IinactApiVersion}) based on FFXIV_ACT_Plugin {{0}}"));
            ilProcessor.Replace(1, Instruction.Create(OpCodes.Ldc_I4_1));
            var stelemIndex = Array.FindIndex(ilProcessor.Body.Instructions.ToArray(),
                                              code => code.OpCode == OpCodes.Stelem_Ref);
            Enumerable.Range(0, 5).ToList().ForEach(_ => ilProcessor.RemoveAt(stelemIndex + 1));
        }

        logfile.WriteOut();
    }

    public void MemoryPlugin()
    {
        var memory = new TargetAssembly(Path.Combine(WorkPath, "FFXIV_ACT_Plugin.Memory.dll"));

        var dataSubscription = memory.Assembly.MainModule.Types.First(type => type.Name == "DataSubscription");
        var delegates = dataSubscription.Methods.Where(method => method.Name.StartsWith("On"));

        var fixes = new List<(string Name, SubscriptionInvoke.Mode Mode, string? Reason)>();
        foreach (var method in delegates)
        {
            var mode = SubscriptionInvoke.Apply(method, memory.Assembly.MainModule, out var reason);
            fixes.Add((method.Name, mode, reason));
        }

        {
            var method = memory.GetMethod(
                "System.Void FFXIV_ACT_Plugin.Memory.ScanMemory::Run(System.Object)");
            var ilProcessor = method.Body.GetILProcessor();
            ilProcessor.Replace(0, Instruction.Create(OpCodes.Ret));
        }
        
        var marshallType = memory.Assembly.MainModule.ImportReference(typeof(System.Runtime.InteropServices.Marshal)).Resolve();
        var marshallCopyBuffer = marshallType.Methods.First(
            m => m.FullName ==
                 "System.Void System.Runtime.InteropServices.Marshal::Copy(System.IntPtr,System.Byte[],System.Int32,System.Int32)");
        var marshallCopyBufferReference = memory.Assembly.MainModule.ImportReference(marshallCopyBuffer);

        void UseMarshallCopyBuffer(MethodDefinition method)
        {
            var originalIl = method.Body.Instructions.ToArray();
            var bufferIndex =
                Array.FindIndex(
                    originalIl,
                    code => code.OpCode == OpCodes.Callvirt && code.Operand.ToString()!.Contains("ReadBuffer"));
            var dynamicLength = originalIl[bufferIndex - 1].OpCode == OpCodes.Conv_I4;
            var offset = dynamicLength ? -3 : 0;
            var ilProcessor = method.Body.GetILProcessor();
            ilProcessor.Replace(bufferIndex, Instruction.Create(OpCodes.Call, marshallCopyBufferReference));
            ilProcessor.InsertAfter(bufferIndex - 2 + offset, Instruction.Create(OpCodes.Ldc_I4_0));
            ilProcessor.RemoveAt(bufferIndex - 6 + offset);
            ilProcessor.RemoveAt(bufferIndex - 6 + offset);
        }

        var readBufferMethods = new[]
        {
            "System.IntPtr FFXIV_ACT_Plugin.Memory.MemoryReader.ReadParty::Read()",
            "System.IntPtr FFXIV_ACT_Plugin.Memory.MemoryReader.ReadZoneMap::Read()",
            "System.IntPtr FFXIV_ACT_Plugin.Memory.MemoryReader.ReadCombatant::Read(System.IntPtr)",
            "System.IntPtr FFXIV_ACT_Plugin.Memory.MemoryReader.ReadPlayer::Read()",
            "System.IntPtr FFXIV_ACT_Plugin.Memory.MemoryReader.ReadMobArray::Read64()"
        };

        foreach (var methodName in readBufferMethods)
            UseMarshallCopyBuffer(memory.GetMethod(methodName));
        
        var intPtrType = memory.Assembly.MainModule.ImportReference(typeof(IntPtr)).Resolve();
        var intPtrOp =
            intPtrType.Methods.First(m => m.FullName == "System.Void* System.IntPtr::op_Explicit(System.IntPtr)");
        var intPtrOpReference = memory.Assembly.MainModule.ImportReference(intPtrOp);
        
        {
            var method = memory.GetMethod(
                "System.UInt32 FFXIV_ACT_Plugin.Memory.MemoryReader.ReadMemory::ReadUInt32(System.IntPtr)");
            var ilProcessor = method.Body.GetILProcessor();
            ilProcessor.Clear();
            ilProcessor.Emit(OpCodes.Ldarg_1);
            ilProcessor.Emit(OpCodes.Call, intPtrOpReference);
            ilProcessor.Emit(OpCodes.Ldind_U4);
            ilProcessor.Emit(OpCodes.Ret);
        }
        
        {
            var method = memory.GetMethod(
                "System.UInt64 FFXIV_ACT_Plugin.Memory.MemoryReader.ReadMemory::ReadUInt64(System.IntPtr)");
            var ilProcessor = method.Body.GetILProcessor();
            ilProcessor.Clear();
            ilProcessor.Emit(OpCodes.Ldarg_1);
            ilProcessor.Emit(OpCodes.Call, intPtrOpReference);
            ilProcessor.Emit(OpCodes.Ldind_I8);
            ilProcessor.Emit(OpCodes.Ret);
        }
        
        {
            var method = memory.GetMethod(
                "System.IntPtr FFXIV_ACT_Plugin.Memory.MemoryReader.ReadMemory::ReadPointer(System.IntPtr)");
            var ilProcessor = method.Body.GetILProcessor();
            ilProcessor.Clear();
            ilProcessor.Emit(OpCodes.Ldarg_1);
            ilProcessor.Emit(OpCodes.Call, intPtrOpReference);
            ilProcessor.Emit(OpCodes.Ldind_I8);
            var intPtrCtor =
                intPtrType.Methods.First(m => m.FullName == "System.Void System.IntPtr::.ctor(System.Int64)");
            var intPtrCtorReference = memory.Assembly.MainModule.ImportReference(intPtrCtor);
            ilProcessor.Emit(OpCodes.Newobj, intPtrCtorReference);
            ilProcessor.Emit(OpCodes.Ret);
        }
        
        // not needed and sometimes throws an exception during early load, see #61
        {
            var method = memory.GetMethod(
                "System.Void FFXIV_ACT_Plugin.Memory.SignatureManager::RefreshVtable()");
            var ilProcessor = method.Body.GetILProcessor();
            ilProcessor.Replace(0, Instruction.Create(OpCodes.Ret));
        }
        
        memory.WriteOut();
        SafeLog(LogInfo, DescribeSubscriptionFixes(MainPluginVersion, fixes));
    }

    private static string DescribeSubscriptionFixes(
        Version? version, List<(string Name, SubscriptionInvoke.Mode Mode, string? Reason)> fixes)
    {
        int Count(SubscriptionInvoke.Mode mode) => fixes.Count(f => f.Mode == mode);
        var message = "[FetchDependencies] FFXIV_ACT_Plugin " + version + " 的 " + fixes.Count +
                      " 個資料事件改為同步通知訂閱者：直接呼叫 " + Count(SubscriptionInvoke.Mode.Direct) +
                      "、只去掉回呼 " + Count(SubscriptionInvoke.Mode.NoCallback) +
                      "、舊做法 " + Count(SubscriptionInvoke.Mode.Legacy) + "。";
        var others = fixes.Where(f => f.Mode != SubscriptionInvoke.Mode.Direct).ToList();
        if (others.Count == 0)
            return message + "通知訂閱者時不再額外配置記憶體。";
        return message + "形狀不符、照較保守的改法：" +
               string.Join("；", others.Select(f => f.Name + "（" + f.Reason + "）")) +
               "。功能不變，只是每次通知會多配置一些記憶體。";
    }

    /// <summary>
    /// 名稱表載入預篩選（見 <see cref="ResourcePrefilter"/>）。要在 <see cref="MainPlugin"/> 之後呼叫；
    /// <see cref="MainPlugin"/> 已在寫出前套用過時只記它留下的那一行。
    /// 不擲例外：任何失敗都只記一行，磁碟上維持 <see cref="MainPlugin"/> 寫出的那份（＝沒有這一步時的結果）。
    /// </summary>
    public void ResourcePlugin()
    {
        if (PrefilterOutcome is { } outcome)
        {
            SafeLog(outcome.Sink, outcome.Message);
            return;
        }

        var dllPath = Path.Combine(WorkPath, ResourcePrefilter.ResourceDllName);
        var hash = ResourceDllSha256;
        if (hash == null || !ResourcePrefilter.VerifiedResourceDlls.TryGetValue(hash, out var verified))
        {
            SafeLog(LogInfo, NotVerifiedMessage(hash));
            return;
        }

        TargetAssembly? resource = null;
        try
        {
            resource = new TargetAssembly(dllPath);
            var failure = ResourcePrefilter.TryApply(resource.Assembly.MainModule);
            if (failure != null)
            {
                SafeLog(LogWarning, RefusedMessage(verified, failure));
                return;
            }

            resource.WriteOut();
            SafeLog(LogInfo, AppliedMessage(verified));
        }
        catch (Exception e)
        {
            SafeLog(LogWarning, FailedMessage(verified, e));
        }
        finally
        {
            try
            {
                resource?.Dispose();
            }
            catch
            {
                // 已寫出或已放棄，釋放失敗不影響結果
            }

            TryDeleteFile(dllPath + ".patched");
        }
    }

    /// <summary>
    /// 在 <see cref="MainPlugin"/> 寫出 Resource DLL 之前就套用預篩選，與 RemoveStrongNaming／MakePublic 同一次寫出：
    /// 約 8 MB 的 DLL 不必寫出後再讀回重寫一次。結果記在 <see cref="PrefilterOutcome"/>，由 <see cref="ResourcePlugin"/>
    /// 照原本的順序記 log。中途擲例外時本體可能改了一半，丟掉、重讀解壓出來的原檔（＝沒有預篩選的結果）。
    /// </summary>
    private TargetAssembly PrefilterBeforeWrite(TargetAssembly resource, string dllPath)
    {
        var hash = ResourceDllSha256;
        if (hash == null || !ResourcePrefilter.VerifiedResourceDlls.TryGetValue(hash, out var verified))
        {
            PrefilterOutcome = (LogInfo, NotVerifiedMessage(hash));
            return resource;
        }

        try
        {
            var failure = ResourcePrefilter.TryApply(resource.Assembly.MainModule);
            PrefilterOutcome = failure != null ? (LogWarning, RefusedMessage(verified, failure)) : (LogInfo, AppliedMessage(verified));
            return resource;
        }
        catch (Exception e)
        {
            PrefilterOutcome = (LogWarning, FailedMessage(verified, e));
        }

        try
        {
            resource.Dispose();
        }
        catch
        {
            // 這份已經不用了，釋放失敗不影響結果
        }

        var fresh = new TargetAssembly(dllPath);
        fresh.RemoveStrongNaming();
        fresh.MakePublic();
        return fresh;
    }

    private string NotVerifiedMessage(string? hash) =>
        "[FetchDependencies] 解析元件 FFXIV_ACT_Plugin " + MainPluginVersion + " 的名稱表（" +
        ResourcePrefilter.ResourceDllName + " SHA-256 " + (hash ?? "讀不到") +
        "）不在已驗證清單，略過載入預篩選；名稱照原樣載入，只是載入時配置較多。";

    private static string RefusedMessage(string verified, string failure) =>
        "[FetchDependencies] " + verified + " 名稱表的載入預篩選沒有套用（" + failure + "）；名稱照原樣載入。";

    private string AppliedMessage(string verified) =>
        "[FetchDependencies] 已對 " + verified + "（主 DLL " + MainPluginVersion +
        "）套用名稱表載入預篩選：技能／狀態名稱不變，只減少每次載入的記憶體配置與 CPU 時間。";

    private static string FailedMessage(string verified, Exception e) =>
        "[FetchDependencies] " + verified + " 名稱表的載入預篩選失敗、維持原樣（" + e.GetType().Name + "：" + e.Message + "）。";

    private static string? TryHashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 殘留的 .patched 不會被載入，下次修補寫出時直接覆蓋
        }
    }

    private static void SafeLog(Action<string>? sink, string message)
    {
        if (sink == null) return;
        try
        {
            sink(message);
        }
        catch
        {
            // 通報失敗不能改變載入結果
        }
    }
}
