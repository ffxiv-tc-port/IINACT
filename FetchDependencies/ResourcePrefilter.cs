using Mono.Cecil;
using Mono.Cecil.Cil;

namespace FetchDependencies;

// 本檔契約：只對清單內（Costura 解壓後、修補前的 SHA-256）的 FFXIV_ACT_Plugin.Resource.dll，
// 在 ResourceFile.LoadResourceDictionary 的 override Join 之前，把內層字典先篩成「值以 _rsv_ 開頭」的項目。
// 等價前提：清單上那一版所有 override 鍵都以 _rsv_ 開頭，Join 本來就只會配到這些項目，配對結果與順序不變。
// 形狀任何一項對不上就回傳原因、一條指令都不動。
internal static class ResourcePrefilter
{
    public const string ResourceDllName = "FFXIV_ACT_Plugin.Resource.dll";

    /// <summary>已離線逐筆比對過 14 份名稱表的 Resource DLL（鍵＝SHA-256 大寫十六進位、值＝顯示用版本）。</summary>
    public static readonly IReadOnlyDictionary<string, string> VerifiedResourceDlls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["10DC07DE16DA355B96154CB11550CAE7146A0124730E87C71466E84FD63FB33C"] = "FFXIV_ACT_Plugin 3.0.3.3",
        };

    private const string KvpUintString = "System.Collections.Generic.KeyValuePair`2<System.UInt32,System.String>";
    private const string KvpStringString = "System.Collections.Generic.KeyValuePair`2<System.String,System.String>";
    private const string UintDictionary = "System.Collections.Generic.SortedDictionary`2<System.UInt32,System.String>";
    private const string StringDictionary = "System.Collections.Generic.SortedDictionary`2<System.String,System.String>";

    /// <returns>null＝已插入；否則是沒插入的原因（此時方法本體保證沒被改動）。</returns>
    public static string? TryApply(ModuleDefinition module)
    {
        var type = module.GetType("FFXIV_ACT_Plugin.Resource.ResourceFile");
        if (type == null) return "找不到 ResourceFile";
        var candidates = type.Methods.Where(m => m.Name == "LoadResourceDictionary" && !m.HasParameters).ToList();
        if (candidates.Count != 1 || !candidates[0].HasBody) return "LoadResourceDictionary 不是唯一或沒有本體";
        var method = candidates[0];
        if (method.ReturnType.FullName != UintDictionary) return "LoadResourceDictionary 回傳型別不符";

        var body = method.Body;
        var il = body.Instructions;

        var joins = Indexes(il, i => IsEnumerableCall(i, "Join", 5, KvpStringString, KvpUintString, "System.String",
                                                      KvpUintString));
        if (joins.Count != 1) return $"Join 呼叫有 {joins.Count} 個";
        var join = joins[0];

        // Join(outer, inner, outerKey, innerKey, result)：inner 之後必須正好是三段快取委派（每段 9 條）。
        var anchor = join - 28;
        if (anchor < 1) return "Join 前的指令不足";
        if (!IsCtorOf(il[anchor - 1], OpCodes.Newobj, StringDictionary)) return "outer 不是 new SortedDictionary<string,string>";
        var dictionary = LoadedLocal(il[anchor], body);
        if (dictionary == null || dictionary.VariableType.FullName != UintDictionary) return "inner 不是 SortedDictionary<uint,string> 區域變數";
        for (var block = anchor + 1; block < join; block += 9)
            if (!IsCachedDelegateLoad(il, block, il[block + 9])) return "Join 的委派參數形狀不符";
        if (il.Count < 2 || il[^1].OpCode != OpCodes.Ret || LoadedLocal(il[^2], body) != dictionary)
            return "回傳的不是 inner 那個字典";

        var wheres = Indexes(il, IsWhereKvpUintString);
        if (wheres.Count != 1) return $"Where<KeyValuePair<uint,string>> 呼叫有 {wheres.Count} 個";
        var where = wheres[0];
        if (where < 9 || !IsCachedDelegateLoad(il, where - 9, il[where])) return "既有 Where 的委派形狀不符";
        var singletonLoad = il[where - 5];
        var predicateLoad = il[where - 4];
        var delegateCtor = il[where - 3];
        if (predicateLoad.Operand is not MethodDefinition predicate || predicate.DeclaringType.DeclaringType != type)
            return "Where 的條件不是 ResourceFile 自己的 lambda";
        // ldc.i4.5＝StringComparison.OrdinalIgnoreCase：它的前綴比對涵蓋 Join 用的 ToLowerInvariant 相等。
        if (!IsRsvPrefixPredicate(predicate)) return $"{predicate.Name} 不是 Value.StartsWith(\"_rsv_\", OrdinalIgnoreCase)";
        if (singletonLoad.Operand is not FieldReference singleton || singleton.FieldType.FullName != predicate.DeclaringType.FullName)
            return "lambda 單例欄位不符";
        if (!IsCtorOf(delegateCtor, OpCodes.Newobj, $"System.Func`2<{KvpUintString},System.Boolean>"))
            return "委派建構子不是 Func<KeyValuePair<uint,string>,bool>";

        var next = il[anchor + 1];
        if (il.Any(i => i.Operand == next || (i.Operand is Instruction[] targets && targets.Contains(next))))
            return "插入點是分支目標";
        if (body.ExceptionHandlers.Any(h => h.TryStart == next || h.TryEnd == next || h.HandlerStart == next ||
                                            h.HandlerEnd == next || h.FilterStart == next))
            return "插入點是例外區塊邊界";
        // 短分支的位移是 1 位元組；跨過插入點的短分支會因為多出的 21 位元組而溢位。
        for (var i = 0; i < il.Count; i++)
            if (il[i].OpCode.OperandType == OperandType.ShortInlineBrTarget &&
                (i <= anchor) != (il.IndexOf((Instruction)il[i].Operand) <= anchor))
                return "有短分支跨過插入點";

        var inserted = new[]
        {
            Instruction.Create(OpCodes.Ldsfld, singleton),
            Instruction.Create(OpCodes.Ldftn, (MethodReference)predicate),
            Instruction.Create(OpCodes.Newobj, (MethodReference)delegateCtor.Operand),
            Instruction.Create(OpCodes.Call, (MethodReference)il[where].Operand),
        };
        var processor = body.GetILProcessor();
        var cursor = il[anchor];
        foreach (var instruction in inserted)
        {
            processor.InsertAfter(cursor, instruction);
            cursor = instruction;
        }

        return null;
    }

    private static List<int> Indexes(IList<Instruction> il, Func<Instruction, bool> match) =>
        Enumerable.Range(0, il.Count).Where(i => match(il[i])).ToList();

    private static bool IsEnumerableCall(Instruction i, string name, int parameterCount, params string[] genericArguments) =>
        i.OpCode == OpCodes.Call && i.Operand is GenericInstanceMethod g && g.Name == name &&
        g.DeclaringType.FullName == "System.Linq.Enumerable" && g.ElementMethod.Parameters.Count == parameterCount &&
        g.GenericArguments.Select(a => a.FullName).SequenceEqual(genericArguments);

    private static bool IsWhereKvpUintString(Instruction i) =>
        IsEnumerableCall(i, "Where", 2, KvpUintString) &&
        ((GenericInstanceMethod)i.Operand).ElementMethod.Parameters[1].ParameterType is GenericInstanceType f &&
        f.ElementType.FullName == "System.Func`2";

    private static bool IsCtorOf(Instruction i, OpCode opCode, string declaringType) =>
        i.OpCode == opCode && i.Operand is MethodReference m && m.Name == ".ctor" && m.DeclaringType.FullName == declaringType;

    /// <summary>C# 快取 lambda 的固定 9 條：ldsfld 快取、dup、brtrue 到 <paramref name="after"/>、pop、ldsfld 單例、ldftn、newobj、dup、stsfld 快取。</summary>
    private static bool IsCachedDelegateLoad(IList<Instruction> il, int start, Instruction after)
    {
        if (start + 9 > il.Count) return false;
        var ops = new[] { OpCodes.Ldsfld, OpCodes.Dup, OpCodes.Brtrue_S, OpCodes.Pop, OpCodes.Ldsfld, OpCodes.Ldftn,
                          OpCodes.Newobj, OpCodes.Dup, OpCodes.Stsfld };
        for (var k = 0; k < 9; k++)
        {
            var op = il[start + k].OpCode;
            if (op != ops[k] && !(k == 2 && op == OpCodes.Brtrue)) return false;
        }

        return il[start + 2].Operand == after && il[start].Operand is FieldReference cache &&
               il[start + 8].Operand is FieldReference store && cache.FullName == store.FullName;
    }

    private static bool IsRsvPrefixPredicate(MethodDefinition m)
    {
        if (m.IsStatic || !m.HasBody || m.ReturnType.FullName != "System.Boolean" || m.Parameters.Count != 1 ||
            m.Parameters[0].ParameterType.FullName != KvpUintString)
            return false;
        var il = m.Body.Instructions;
        return il.Count == 6 &&
               il[0].OpCode == OpCodes.Ldarga_S && il[0].Operand == m.Parameters[0] &&
               il[1].OpCode == OpCodes.Call && il[1].Operand is MethodReference getValue && getValue.Name == "get_Value" &&
               getValue.DeclaringType.FullName == KvpUintString &&
               il[2].OpCode == OpCodes.Ldstr && (string)il[2].Operand == "_rsv_" &&
               il[3].OpCode == OpCodes.Ldc_I4_5 &&
               il[4].OpCode == OpCodes.Callvirt && il[4].Operand is MethodReference startsWith &&
               startsWith.FullName == "System.Boolean System.String::StartsWith(System.String,System.StringComparison)" &&
               il[5].OpCode == OpCodes.Ret;
    }

    private static VariableDefinition? LoadedLocal(Instruction i, MethodBody body)
    {
        var index = i.OpCode.Code switch
        {
            Code.Ldloc_0 => 0,
            Code.Ldloc_1 => 1,
            Code.Ldloc_2 => 2,
            Code.Ldloc_3 => 3,
            Code.Ldloc_S or Code.Ldloc => (i.Operand as VariableDefinition)?.Index ?? -1,
            _ => -1,
        };
        return index >= 0 && index < body.Variables.Count ? body.Variables[index] : null;
    }
}
