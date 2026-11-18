using Mono.Cecil;
using Mono.Cecil.Cil;

namespace FetchDependencies;

// 本檔契約：CombatantDataExtension.DirectHeal 迴圈裡的 (long)((Dnum)sum + swing.Damage) 改成
// Dnum.AddAsDnum(sum, (long)swing.Damage)：結果相同（等價說明在 Dnum.AddAsDnum），只是每筆治療不再配置兩個 Dnum。
// 三條指令就地換 opcode／operand、不增刪指令，分支目標不受影響；形狀任何一項對不上就回傳原因、一條指令都不動。
// AddAsDnum 是 internal，靠 NotACT 的 InternalsVisibleTo("FFXIV_ACT_Plugin") 存取。
internal static class DnumSumPatch
{
    private const string Dnum = "Advanced_Combat_Tracker.Dnum";
    private const string Int64 = "System.Int64";

    /// <returns>null＝已改寫；否則是沒改寫的原因（此時方法本體保證沒被改動）。</returns>
    public static string? TryApply(ModuleDefinition module)
    {
        var type = module.GetType("FFXIV_ACT_Plugin.CombatantDataExtension");
        if (type == null) return "找不到 CombatantDataExtension";
        var candidates = type.Methods.Where(m => m.Name == "DirectHeal").ToList();
        if (candidates.Count != 1 || !candidates[0].HasBody) return "DirectHeal 不是唯一或沒有本體";
        var body = candidates[0].Body;
        if (body.HasExceptionHandlers) return "DirectHeal 有例外處理區塊";
        var il = body.Instructions;

        var hits = new List<int>();
        for (var i = 0; i + 6 < il.Count; i++)
        {
            if (IsCall(il[i], "op_Implicit", Dnum, Int64) &&
                IsCall(il[i + 5], "op_Addition", Dnum, Dnum, Dnum) &&
                IsCall(il[i + 6], "op_Implicit", Int64, Dnum))
                hits.Add(i);
        }

        if (hits.Count != 1) return "DirectHeal 裡符合的加總有 " + hits.Count + " 處（應為 1）";
        var k = hits[0];

        // 中間四條必須正好推上一個 Dnum：ldloc 清單、ldloc 索引、get_Item、get_Damage。
        if (!IsLdloc(il[k + 1]) || !IsLdloc(il[k + 2]) ||
            !IsListGetItem(il[k + 3]) || !IsGetDamage(il[k + 4]))
            return "加總兩運算元之間的指令不符";

        var window = new HashSet<Instruction>(il.Skip(k + 1).Take(6));
        if (il.Any(x => x.Operand is Instruction t && window.Contains(t) ||
                        x.Operand is Instruction[] ts && ts.Any(window.Contains)))
            return "有分支跳進加總中間";

        var toLong = (MethodReference)il[k + 6].Operand;
        var dnumType = ((MethodReference)il[k].Operand).DeclaringType;
        var addAsDnum = new MethodReference("AddAsDnum", module.TypeSystem.Int64, dnumType) { HasThis = false };
        addAsDnum.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int64));
        addAsDnum.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int64));

        il[k].OpCode = OpCodes.Nop;
        il[k].Operand = null;
        il[k + 5].OpCode = OpCodes.Call;
        il[k + 5].Operand = toLong;
        il[k + 6].OpCode = OpCodes.Call;
        il[k + 6].Operand = addAsDnum;
        return null;
    }

    private static bool IsCall(Instruction i, string name, string returns, params string[] parameters) =>
        i.OpCode == OpCodes.Call && Matches(i.Operand as MethodReference, name, Dnum, returns, parameters);

    private static bool IsListGetItem(Instruction i) =>
        i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference { Name: "get_Item", HasThis: true } m &&
        m.Parameters.Count == 1 && m.DeclaringType is GenericInstanceType t &&
        t.ElementType.FullName == "System.Collections.Generic.List`1" &&
        t.GenericArguments.Count == 1 && t.GenericArguments[0].FullName == "Advanced_Combat_Tracker.MasterSwing";

    private static bool IsGetDamage(Instruction i) =>
        i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference { Name: "get_Damage", HasThis: true } m &&
        !m.HasParameters && m.DeclaringType.FullName == "Advanced_Combat_Tracker.MasterSwing" &&
        m.ReturnType.FullName == Dnum;

    private static bool Matches(MethodReference? m, string name, string declaringType, string returns, string[] parameters) =>
        m != null && m.Name == name && !m.HasThis && m.DeclaringType.FullName == declaringType &&
        m.ReturnType.FullName == returns &&
        m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters);

    private static bool IsLdloc(Instruction i) =>
        i.OpCode == OpCodes.Ldloc_0 || i.OpCode == OpCodes.Ldloc_1 || i.OpCode == OpCodes.Ldloc_2 ||
        i.OpCode == OpCodes.Ldloc_3 || i.OpCode == OpCodes.Ldloc_S || i.OpCode == OpCodes.Ldloc;
}
