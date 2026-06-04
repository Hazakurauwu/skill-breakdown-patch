using System;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// ShinraMeter rotation patcher.
//   pass1 <inDll> <outDll>            : add Members.dealtSkillLog field + InternalsVisibleTo("ShinraRotationPatch")
//   pass2 <inDll> <helperDll> <out>   : insert call RotationEnricher.Enrich(stats) into AutomatedExport(NpcEntity,..)

static class Program
{
    const string HelperAsmName = "ShinraRotationPatch";

    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("need a command"); return 2; }
        switch (args[0])
        {
            case "pass1": return Pass1(args[1], args[2]);
            case "pass2": return Pass2(args[1], args[2], args[3]);
            case "dump": return Dump(args[1]);
            case "sn":
                foreach (var f in args.Skip(1))
                {
                    var mm = ModuleDefMD.Load(f);
                    var pk = mm.Assembly.PublicKey;
                    bool empty = PublicKeyBase.IsNullOrEmpty2(pk);
                    Console.WriteLine(System.IO.Path.GetFileName(f) + " : " +
                        (empty ? "<no public key>" : (pk.Data?.Length ?? 0) + "-byte key") +
                        " | " + mm.Assembly.FullName);
                }
                return 0;
            default: Console.Error.WriteLine("unknown command " + args[0]); return 2;
        }
    }

    static int Pass1(string inDll, string outDll)
    {
        var mod = ModuleDefMD.Load(inDll);

        // --- add public field: List<JsonSkill> dealtSkillLog to Members ---
        var members = mod.Types.First(t => t.FullName == "DamageMeter.TeraDpsApi.Members");
        if (members.Fields.Any(f => f.Name == "dealtSkillLog"))
        {
            Console.WriteLine("dealtSkillLog already present, skipping field add");
        }
        else
        {
            var jsonSkill = mod.Types.First(t => t.FullName == "DamageMeter.TeraDpsApi.JsonSkill");
            // reuse the exact List`1 reference (correct corlib scope) from an existing List<> field
            var sampleList = members.Fields
                .Select(f => f.FieldSig.Type as GenericInstSig)
                .First(g => g != null && g.GenericType.TypeName == "List`1");
            var listGen = new GenericInstSig((ClassOrValueTypeSig)sampleList.GenericType, jsonSkill.ToTypeSig());
            var field = new FieldDefUser("dealtSkillLog",
                new FieldSig(listGen),
                FieldAttributes.Public);
            members.Fields.Add(field);
            Console.WriteLine("added Members.dealtSkillLog (List`1 from " + sampleList.GenericType.TypeName + ")");
        }

        // --- add [assembly: InternalsVisibleTo("ShinraRotationPatch")] ---
        AddInternalsVisibleTo(mod, HelperAsmName);

        mod.Write(outDll);
        Console.WriteLine("pass1 written: " + outDll);
        return 0;
    }

    static void AddInternalsVisibleTo(ModuleDefMD mod, string asmName)
    {
        var asm = mod.Assembly;
        var attrTypeRef = new TypeRefUser(mod, "System.Runtime.CompilerServices",
            "InternalsVisibleToAttribute", mod.CorLibTypes.AssemblyRef);
        // already present?
        bool exists = asm.CustomAttributes.Any(ca =>
            ca.TypeFullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute" &&
            ca.ConstructorArguments.Count == 1 &&
            (ca.ConstructorArguments[0].Value?.ToString() ?? "").StartsWith(asmName));
        if (exists) { Console.WriteLine("InternalsVisibleTo already present"); return; }

        var ctorSig = MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.String);
        var ctorRef = new MemberRefUser(mod, ".ctor", ctorSig, attrTypeRef);
        var ca2 = new CustomAttribute(ctorRef,
            new[] { new CAArgument(mod.CorLibTypes.String, asmName) });
        asm.CustomAttributes.Add(ca2);
        Console.WriteLine("added InternalsVisibleTo(" + asmName + ")");
    }

    static int Pass2(string inDll, string helperDll, string outDll)
    {
        var mod = ModuleDefMD.Load(inDll);
        var helper = ModuleDefMD.Load(helperDll);

        // resolve RotationEnricher.Enrich(ExtendedStats)
        var enricher = helper.Types.First(t => t.FullName == "ShinraRotationPatch.RotationEnricher");
        var enrichDef = enricher.Methods.First(m => m.Name == "Enrich" && m.Parameters.Count == 1);

        // build a MemberRef into `mod` pointing at the helper method.
        // parameter type = DamageMeter's own ExtendedStats TypeDef (same module).
        var extStats = mod.Types.First(t => t.FullName == "DamageMeter.TeraDpsApi.ExtendedStats");
        var helperAsmRef = new AssemblyRefUser(helper.Assembly);
        var enricherRef = new TypeRefUser(mod, "ShinraRotationPatch", "RotationEnricher", helperAsmRef);
        var enrichSig = MethodSig.CreateStatic(mod.CorLibTypes.Void, extStats.ToTypeSig());
        var enrichRef = new MemberRefUser(mod, "Enrich", enrichSig, enricherRef);

        // find AutomatedExport(NpcEntity, AbnormalityStorage)
        var de = mod.Types.First(t => t.FullName == "DamageMeter.DataExporter");
        var m = de.Methods.First(x => x.Name == "AutomatedExport"
            && x.Parameters.Count == 2
            && x.Parameters[0].Type.FullName == "Tera.Game.NpcEntity");

        var body = m.Body;
        var instrs = body.Instructions;

        // 1) find call to GenerateStats
        int callIdx = -1;
        for (int i = 0; i < instrs.Count; i++)
        {
            if (instrs[i].OpCode == OpCodes.Call &&
                instrs[i].Operand is IMethod im && im.Name == "GenerateStats")
            { callIdx = i; break; }
        }
        if (callIdx < 0) { Console.Error.WriteLine("GenerateStats call not found"); return 3; }

        // 2) find the null-check branch after GenerateStats
        int branchIdx = -1;
        for (int i = callIdx + 1; i < instrs.Count && i < callIdx + 12; i++)
        {
            var oc = instrs[i].OpCode;
            if (oc == OpCodes.Brtrue || oc == OpCodes.Brtrue_S ||
                oc == OpCodes.Brfalse || oc == OpCodes.Brfalse_S)
            { branchIdx = i; break; }
        }
        if (branchIdx < 0) { Console.Error.WriteLine("null-check branch not found"); return 3; }

        // 3) capture the instruction run that loads `stats` for the null check.
        //    display-class shape: ldloc.0 ; ldfld stats     (2 instrs)
        //    plain-local shape:   ldloc stats               (1 instr)
        int loadStart;
        if (instrs[branchIdx - 1].OpCode == OpCodes.Ldfld)
            loadStart = branchIdx - 2;
        else
            loadStart = branchIdx - 1;
        if (loadStart < callIdx) { Console.Error.WriteLine("stats load run not found"); return 3; }

        // 4) not-null path anchor
        var br = instrs[branchIdx];
        Instruction insertBefore;
        if (br.OpCode == OpCodes.Brtrue || br.OpCode == OpCodes.Brtrue_S)
            insertBefore = (Instruction)br.Operand;       // not-null path is the branch target
        else
            insertBefore = instrs[branchIdx + 1];          // brfalse -> not-null path is fallthrough

        int at = instrs.IndexOf(insertBefore);
        if (at < 0) { Console.Error.WriteLine("insert anchor not found in stream"); return 3; }

        // 5) build inserted sequence: <clone of stats-load> ; call Enrich
        var inserted = new System.Collections.Generic.List<Instruction>();
        for (int i = loadStart; i < branchIdx; i++)
            inserted.Add(CloneInstr(instrs[i]));
        inserted.Add(OpCodes.Call.ToInstruction(enrichRef));

        for (int i = 0; i < inserted.Count; i++)
            instrs.Insert(at + i, inserted[i]);

        var firstInserted = inserted[0];
        // retarget any branch that pointed at the old anchor to our first inserted instr
        foreach (var ins in instrs)
        {
            if (inserted.Contains(ins)) continue;
            if (ins.Operand is Instruction tgt && tgt == insertBefore)
                ins.Operand = firstInserted;
            else if (ins.Operand is Instruction[] arr)
            {
                for (int k = 0; k < arr.Length; k++)
                    if (arr[k] == insertBefore) arr[k] = firstInserted;
            }
        }

        body.OptimizeBranches();
        body.OptimizeMacros();

        mod.Write(outDll);
        Console.WriteLine($"pass2 inserted Enrich call (loadRun {branchIdx - loadStart} instr, anchor idx {at}); written: " + outDll);
        return 0;
    }

    static Instruction CloneInstr(Instruction ins)
    {
        return ins.Operand == null
            ? new Instruction(ins.OpCode)
            : new Instruction(ins.OpCode, ins.Operand);
    }

    static int Dump(string inDll)
    {
        var mod = ModuleDefMD.Load(inDll);
        var de = mod.Types.First(t => t.FullName == "DamageMeter.DataExporter");
        var m = de.Methods.First(x => x.Name == "AutomatedExport"
            && x.Parameters.Count == 2
            && x.Parameters[0].Type.FullName == "Tera.Game.NpcEntity");
        Console.WriteLine("Method: " + m.FullName);
        foreach (var v in m.Body.Variables)
            Console.WriteLine("  local " + v.Index + " : " + v.Type.FullName);
        int i = 0;
        foreach (var ins in m.Body.Instructions)
        {
            Console.WriteLine($"  {i,3}: {ins.OpCode,-12} {ins.Operand}");
            i++;
        }
        return 0;
    }
}
