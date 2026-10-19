using Mono.Cecil;
using Mono.Cecil.Cil;

namespace FetchDependencies;

// 本檔契約：把 FFXIV_ACT_Plugin.Memory.DataSubscription.On* 裡的委派 BeginInvoke（.NET Core 不支援）改成同步 Invoke。
// 依序嘗試三種改法；前兩種只要形狀有一項不符就一條指令都不動，換下一種：
//   Direct：GetInvocationList 逐一 BeginInvoke 的迴圈收成一次多播 Invoke（順序不變；迴圈本來就沒有 try，例外照樣中斷後續並往外傳）。
//   NoCallback：只拿掉 AsyncCallback 與 state 兩個參數的建構。
//   Legacy：舊做法，在呼叫前插兩個 pop（每個訂閱者每次照樣配置一個 AsyncCallback）。
internal static class SubscriptionInvoke
{
    internal enum Mode
    {
        Direct,
        NoCallback,
        Legacy,
    }

    /// <param name="fallbackReason">沒用 Direct 的原因；用了 Direct 時為 null。</param>
    public static Mode Apply(MethodDefinition method, ModuleDefinition module, out string? fallbackReason)
    {
        var body = method.Body;
        var il = body.Instructions;
        var begin = -1;
        for (var i = 0; i < il.Count; i++)
        {
            if (il[i].OpCode == OpCodes.Callvirt && il[i].Operand.ToString()!.Contains("BeginInvoke"))
            {
                begin = i;
                break;
            }
        }

        if (begin == -1)
            throw new DllNotFoundException("Could not find BeginInvoke instruction");
        var beginInvoke = (MethodReference)il[begin].Operand;
        var invokeMethod = beginInvoke.DeclaringType.Resolve().Methods.First(m => m.Name == "Invoke");
        var invoke = module.ImportReference(invokeMethod);

        fallbackReason = CheckCallbackGroup(body, begin, beginInvoke, invokeMethod);
        if (fallbackReason != null)
        {
            ApplyLegacy(body, begin, invoke);
            return Mode.Legacy;
        }

        fallbackReason = CheckLoop(body, begin, beginInvoke, invokeMethod, out var g);
        if (fallbackReason == null)
        {
            CollapseLoop(body, begin, g, invoke);
            return Mode.Direct;
        }

        RemoveCallbackGroup(body, begin, invoke);
        return Mode.NoCallback;
    }

    private static void ApplyLegacy(MethodBody body, int begin, MethodReference invoke)
    {
        var il = body.Instructions;
        var invokeInstruction = il[begin];
        var ilProcessor = body.GetILProcessor();
        ilProcessor.RemoveAt(begin + 1);
        ilProcessor.InsertBefore(invokeInstruction, Instruction.Create(OpCodes.Pop));
        ilProcessor.InsertBefore(invokeInstruction, Instruction.Create(OpCodes.Pop));
        ilProcessor.Replace(invokeInstruction, Instruction.Create(OpCodes.Callvirt, invoke));
    }

    // 期望：ldarg.0 / ldftn / newobj AsyncCallback::.ctor / ldnull / callvirt BeginInvoke / pop
    private static string? CheckCallbackGroup(MethodBody body, int begin, MethodReference beginInvoke,
                                              MethodDefinition invoke)
    {
        var il = body.Instructions;
        if (body.HasExceptionHandlers) return "方法有例外處理區塊";
        if (begin < 4 || begin + 1 >= il.Count) return "BeginInvoke 前後的指令不足";
        if (il[begin - 4].OpCode != OpCodes.Ldarg_0) return "回呼的目標物件不是 this";
        if (il[begin - 3].OpCode != OpCodes.Ldftn) return "回呼前不是 ldftn";
        if (il[begin - 2].OpCode != OpCodes.Newobj || il[begin - 2].Operand is not MethodReference ctor ||
            ctor.DeclaringType.FullName != "System.AsyncCallback")
            return "沒有建 AsyncCallback";
        if (il[begin - 1].OpCode != OpCodes.Ldnull) return "state 參數不是 null";
        if (il[begin + 1].OpCode != OpCodes.Pop) return "BeginInvoke 的回傳值沒有被丟棄";
        if (invoke.ReturnType.FullName != "System.Void") return "Invoke 有回傳值";
        var ps = beginInvoke.Parameters;
        if (ps.Count != invoke.Parameters.Count + 2 || ps[^2].ParameterType.FullName != "System.AsyncCallback" ||
            ps[^1].ParameterType.FullName != "System.Object")
            return "BeginInvoke 的參數不是 Invoke 的參數加上回呼與 state";
        for (var i = begin - 3; i <= begin + 1; i++)
            if (IsBranchTarget(il, il[i]))
                return "有分支跳進回呼建構的中間";
        return null;
    }

    private static void RemoveCallbackGroup(MethodBody body, int begin, MethodReference invoke)
    {
        var il = body.Instructions;
        var call = il[begin];
        var remove = new[] { il[begin - 4], il[begin - 3], il[begin - 2], il[begin - 1], il[begin + 1] };
        // 跳到回呼建構開頭的分支，此時堆疊上已是委派與 Invoke 的參數。
        Retarget(il, il[begin - 4], call);
        call.OpCode = OpCodes.Callvirt;
        call.Operand = invoke;
        foreach (var instruction in remove)
            il.Remove(instruction);
    }

    // 期望（g＝GetInvocationList）：
    //   ldarg.0 / ldfld F / [g] callvirt GetInvocationList / stloc A / ldc.i4.0 / stloc I / br COND
    //   LOOP: ldloc A / ldloc I / ldelem.ref / castclass D / dup / brtrue CALL / pop / br NEXT
    //   CALL: 參數（只讀引數或 A、I 以外的區域變數）/ 回呼建構 / callvirt BeginInvoke / pop
    //   NEXT: ldloc I / ldc.i4.1 / add / stloc I / COND: ldloc I / ldloc A / ldlen / conv.i4 / blt LOOP
    private static string? CheckLoop(MethodBody body, int begin, MethodReference beginInvoke, MethodDefinition invoke,
                                     out int g)
    {
        var il = body.Instructions;
        var delegateType = beginInvoke.DeclaringType.FullName;
        var calls = Enumerable.Range(0, il.Count).Where(i => il[i].Operand is MethodReference
        {
            Name: "GetInvocationList", DeclaringType.FullName: "System.Delegate",
        }).ToList();
        g = calls.Count == 1 ? calls[0] : -1;
        if (g == -1) return $"GetInvocationList 呼叫有 {calls.Count} 個";
        var end = begin + 10;
        if (g < 2 || end >= il.Count || g + 13 + invoke.Parameters.Count != begin - 4)
            return "迴圈與參數的指令數不符";
        if (il[g - 2].OpCode != OpCodes.Ldarg_0 || il[g - 1].OpCode != OpCodes.Ldfld ||
            ((FieldReference)il[g - 1].Operand).FieldType.FullName != delegateType)
            return "GetInvocationList 前不是讀事件欄位";

        var a = Local(il[g + 1], store: true);
        var i = Local(il[g + 3], store: true);
        if (a < 0 || i < 0 || a == i || il[g + 2].OpCode != OpCodes.Ldc_I4_0) return "迴圈初始化形狀不符";
        if (!IsBranch(il[g + 4], OpCodes.Br, OpCodes.Br_S, il[begin + 6])) return "迴圈入口不是跳到條件判斷";
        if (Local(il[g + 5], store: false) != a || Local(il[g + 6], store: false) != i ||
            il[g + 7].OpCode != OpCodes.Ldelem_Ref || il[g + 8].OpCode != OpCodes.Castclass ||
            ((TypeReference)il[g + 8].Operand).FullName != delegateType || il[g + 9].OpCode != OpCodes.Dup ||
            !IsBranch(il[g + 10], OpCodes.Brtrue, OpCodes.Brtrue_S, il[g + 13]) || il[g + 11].OpCode != OpCodes.Pop ||
            !IsBranch(il[g + 12], OpCodes.Br, OpCodes.Br_S, il[begin + 2]))
            return "取出訂閱者的形狀不符";
        for (var k = g + 13; k < begin - 4; k++)
        {
            var load = Local(il[k], store: false);
            if (!(IsArgLoad(il[k]) || (load >= 0 && load != a && load != i))) return "參數不是單純讀引數或區域變數";
        }

        if (Local(il[begin + 2], store: false) != i || il[begin + 3].OpCode != OpCodes.Ldc_I4_1 ||
            il[begin + 4].OpCode != OpCodes.Add || Local(il[begin + 5], store: true) != i ||
            Local(il[begin + 6], store: false) != i || Local(il[begin + 7], store: false) != a ||
            il[begin + 8].OpCode != OpCodes.Ldlen || il[begin + 9].OpCode != OpCodes.Conv_I4 ||
            !IsBranch(il[end], OpCodes.Blt, OpCodes.Blt_S, il[g + 5]))
            return "遞增與條件判斷的形狀不符";

        var inside = new HashSet<Instruction>(Enumerable.Range(g - 1, end - g + 2).Select(k => il[k]));
        for (var k = 0; k < il.Count; k++)
        {
            if (k >= g - 2 && k <= end) continue;
            if (Targets(il[k]).Any(inside.Contains)) return "迴圈外有分支跳進迴圈";
        }

        return null;
    }

    private static void CollapseLoop(MethodBody body, int begin, int g, MethodReference invoke)
    {
        var il = body.Instructions;
        var call = il[begin];
        var remove = Enumerable.Range(g, 13)
                               .Concat(Enumerable.Range(begin - 4, 4))
                               .Concat(Enumerable.Range(begin + 1, 10))
                               .Select(k => il[k]).ToList();
        call.OpCode = OpCodes.Callvirt;
        call.Operand = invoke;
        foreach (var instruction in remove)
            il.Remove(instruction);
    }

    private static bool IsBranchTarget(IList<Instruction> il, Instruction target) =>
        il.Any(x => Targets(x).Contains(target));

    private static IEnumerable<Instruction> Targets(Instruction x) => x.Operand switch
    {
        Instruction t => new[] { t },
        Instruction[] ts => ts,
        _ => Array.Empty<Instruction>(),
    };

    private static void Retarget(IList<Instruction> il, Instruction from, Instruction to)
    {
        foreach (var x in il)
        {
            if (x.Operand == from)
                x.Operand = to;
            else if (x.Operand is Instruction[] ts)
                for (var k = 0; k < ts.Length; k++)
                    if (ts[k] == from) ts[k] = to;
        }
    }

    private static bool IsBranch(Instruction x, OpCode longForm, OpCode shortForm, Instruction target) =>
        (x.OpCode == longForm || x.OpCode == shortForm) && x.Operand == target;

    private static bool IsArgLoad(Instruction x) =>
        x.OpCode == OpCodes.Ldarg_0 || x.OpCode == OpCodes.Ldarg_1 || x.OpCode == OpCodes.Ldarg_2 ||
        x.OpCode == OpCodes.Ldarg_3 || x.OpCode == OpCodes.Ldarg_S || x.OpCode == OpCodes.Ldarg;

    /// <returns>區域變數索引；不是對應的 ldloc／stloc 時為 -1。</returns>
    private static int Local(Instruction x, bool store)
    {
        var c = x.OpCode;
        if (store)
        {
            if (c == OpCodes.Stloc_0) return 0;
            if (c == OpCodes.Stloc_1) return 1;
            if (c == OpCodes.Stloc_2) return 2;
            if (c == OpCodes.Stloc_3) return 3;
            return c == OpCodes.Stloc_S || c == OpCodes.Stloc ? ((VariableDefinition)x.Operand).Index : -1;
        }

        if (c == OpCodes.Ldloc_0) return 0;
        if (c == OpCodes.Ldloc_1) return 1;
        if (c == OpCodes.Ldloc_2) return 2;
        if (c == OpCodes.Ldloc_3) return 3;
        return c == OpCodes.Ldloc_S || c == OpCodes.Ldloc ? ((VariableDefinition)x.Operand).Index : -1;
    }
}
