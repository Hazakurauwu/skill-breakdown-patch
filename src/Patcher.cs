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
            case "mergeinject": return MergeInject(args[1], args[2], args[3]);
            case "verify": return Verify(args[1]);
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

    // mergeinject: merge RotationEnricher INTO DamageMeter.dll (no external assembly)
    // then inject the call to the now-internal Enrich. Solves the FileNotFoundException
    // on meters whose runtime only loads assemblies listed in deps.json (e.g. Asura).
    static int MergeInject(string inDll, string helperDll, string outDll)
    {
        var mod = ModuleDefMD.Load(inDll);
        var helper = ModuleDefMD.Load(helperDll);

        // ---- 1) MERGE: move RotationEnricher into the DamageMeter module ----
        var rotType = helper.Types.First(t => t.FullName == "ShinraRotationPatch.RotationEnricher");
        helper.Types.Remove(rotType);

        var importer = new Importer(mod, ImporterOptions.TryToUseTypeDefs);

        if (rotType.BaseType != null)
            rotType.BaseType = RemapTD(rotType.BaseType, importer, mod);

        foreach (var method in rotType.Methods)
        {
            if (method.MethodSig != null)
            {
                method.MethodSig.RetType = RemapSig(method.MethodSig.RetType, importer, mod);
                for (int i = 0; i < method.MethodSig.Params.Count; i++)
                    method.MethodSig.Params[i] = RemapSig(method.MethodSig.Params[i], importer, mod);
            }
            var body = method.Body;
            if (body == null) continue;
            foreach (var local in body.Variables)
                local.Type = RemapSig(local.Type, importer, mod);
            foreach (var eh in body.ExceptionHandlers)
                if (eh.CatchType != null)
                    eh.CatchType = RemapTD(eh.CatchType, importer, mod);
            foreach (var instr in body.Instructions)
            {
                switch (instr.Operand)
                {
                    case ITypeDefOrRef td: instr.Operand = RemapTD(td, importer, mod); break;
                    case MemberRef mr:     instr.Operand = RemapMember(mr, importer, mod); break;
                    case MethodSpec msp:   instr.Operand = RemapMethodSpec(msp, importer, mod); break;
                    case IMethod im:       instr.Operand = importer.Import(im); break;
                    case IField ifld:      instr.Operand = importer.Import(ifld); break;
                }
            }
        }

        mod.Types.Add(rotType);
        Console.WriteLine("merged RotationEnricher into DamageMeter module");

        var enrichDef = rotType.Methods.First(x => x.Name == "Enrich" && x.Parameters.Count == 1);

        // ---- 2) INJECT the call into AutomatedExport (same point as pass2) ----
        var de = mod.Types.First(t => t.FullName == "DamageMeter.DataExporter");
        var m = de.Methods.First(x => x.Name == "AutomatedExport"
            && x.Parameters.Count == 2
            && x.Parameters[0].Type.FullName == "Tera.Game.NpcEntity");

        var body2 = m.Body;
        var instrs = body2.Instructions;

        int callIdx = -1;
        for (int i = 0; i < instrs.Count; i++)
            if (instrs[i].OpCode == OpCodes.Call && instrs[i].Operand is IMethod gm && gm.Name == "GenerateStats")
            { callIdx = i; break; }
        if (callIdx < 0) { Console.Error.WriteLine("GenerateStats call not found"); return 3; }

        int branchIdx = -1;
        for (int i = callIdx + 1; i < instrs.Count && i < callIdx + 12; i++)
        {
            var oc = instrs[i].OpCode;
            if (oc == OpCodes.Brtrue || oc == OpCodes.Brtrue_S || oc == OpCodes.Brfalse || oc == OpCodes.Brfalse_S)
            { branchIdx = i; break; }
        }
        if (branchIdx < 0) { Console.Error.WriteLine("null-check branch not found"); return 3; }

        int loadStart = (instrs[branchIdx - 1].OpCode == OpCodes.Ldfld) ? branchIdx - 2 : branchIdx - 1;
        if (loadStart < callIdx) { Console.Error.WriteLine("stats load run not found"); return 3; }

        var br = instrs[branchIdx];
        Instruction insertBefore = (br.OpCode == OpCodes.Brtrue || br.OpCode == OpCodes.Brtrue_S)
            ? (Instruction)br.Operand
            : instrs[branchIdx + 1];

        int at = instrs.IndexOf(insertBefore);
        if (at < 0) { Console.Error.WriteLine("insert anchor not found"); return 3; }

        var inserted = new System.Collections.Generic.List<Instruction>();
        for (int i = loadStart; i < branchIdx; i++)
            inserted.Add(CloneInstr(instrs[i]));
        inserted.Add(OpCodes.Call.ToInstruction(enrichDef));

        for (int i = 0; i < inserted.Count; i++)
            instrs.Insert(at + i, inserted[i]);

        var firstInserted = inserted[0];
        foreach (var ins in instrs)
        {
            if (inserted.Contains(ins)) continue;
            if (ins.Operand is Instruction tgt && tgt == insertBefore)
                ins.Operand = firstInserted;
            else if (ins.Operand is Instruction[] arr)
                for (int k = 0; k < arr.Length; k++)
                    if (arr[k] == insertBefore) arr[k] = firstInserted;
        }

        body2.OptimizeBranches();
        body2.OptimizeMacros();

        mod.Write(outDll);
        Console.WriteLine("mergeinject done: " + outDll);
        return 0;
    }

    // ---- Manual remappers: prefer the INTERNAL TypeDef (by full name) over the
    // ---- importer, which leaves references scoped to the source helper module
    // ---- (causing "ShinraRotationPatch.dll cannot be loaded" at JIT time).

    static TypeDef FindInternal(ModuleDef dm, string fullName)
    {
        foreach (var t in dm.GetTypes())
            if (t.FullName == fullName) return t;
        return null;
    }

    static ITypeDefOrRef RemapTD(ITypeDefOrRef t, Importer imp, ModuleDef dm)
    {
        if (t == null) return null;
        if (t is TypeSpec ts)                              // e.g. List<JsonSkill> as a declaring type
            return new TypeSpecUser(RemapSig(ts.TypeSig, imp, dm));
        var td = FindInternal(dm, t.FullName);
        return td != null ? (ITypeDefOrRef)td : imp.Import(t);
    }

    static TypeSig RemapSig(TypeSig sig, Importer imp, ModuleDef dm)
    {
        switch (sig)
        {
            case null: return null;
            case GenericInstSig gi:
            {
                var gt = (ClassOrValueTypeSig)RemapSig(gi.GenericType, imp, dm);
                var ng = new GenericInstSig(gt, gi.GenericArguments.Count);
                foreach (var a in gi.GenericArguments) ng.GenericArguments.Add(RemapSig(a, imp, dm));
                return ng;
            }
            case SZArraySig sz: return new SZArraySig(RemapSig(sz.Next, imp, dm));
            case ArraySig ar:   return new ArraySig(RemapSig(ar.Next, imp, dm), ar.Rank);
            case ByRefSig br:   return new ByRefSig(RemapSig(br.Next, imp, dm));
            case PtrSig p:      return new PtrSig(RemapSig(p.Next, imp, dm));
            case ClassSig c:
            {
                var td = FindInternal(dm, c.TypeDefOrRef.FullName);
                return td != null ? new ClassSig(td) : imp.Import(sig);
            }
            case ValueTypeSig v:
            {
                var td = FindInternal(dm, v.TypeDefOrRef.FullName);
                return td != null ? new ValueTypeSig(td) : imp.Import(sig);
            }
            default: return imp.Import(sig);                // corlib primitives, generic vars, etc.
        }
    }

    static IMemberRef RemapMember(MemberRef mr, Importer imp, ModuleDef dm)
    {
        var decl = mr.Class as ITypeDefOrRef;
        var newDecl = (decl != null) ? RemapTD(decl, imp, dm) : null;
        if (mr.MethodSig != null)
        {
            var ms = mr.MethodSig;
            var nm = new MethodSig(ms.CallingConvention, ms.GenParamCount, RemapSig(ms.RetType, imp, dm));
            foreach (var p in ms.Params) nm.Params.Add(RemapSig(p, imp, dm));
            return new MemberRefUser(dm, mr.Name, nm, newDecl);
        }
        var fs = new FieldSig(RemapSig(mr.FieldSig.Type, imp, dm));
        return new MemberRefUser(dm, mr.Name, fs, newDecl);
    }

    static IMethod RemapMethodSpec(MethodSpec msp, Importer imp, ModuleDef dm)
    {
        // generic method instantiation (e.g. List<JsonSkill>.Add): remap the base
        // method ref + the type arguments so JsonSkill etc. resolve internally.
        IMethodDefOrRef baseMethod = msp.Method is MemberRef mr
            ? (IMethodDefOrRef)RemapMember(mr, imp, dm)
            : (IMethodDefOrRef)imp.Import(msp.Method);
        var gsig = msp.GenericInstMethodSig;
        if (gsig == null) return baseMethod;
        var ng = new GenericInstMethodSig();
        foreach (var a in gsig.GenericArguments) ng.GenericArguments.Add(RemapSig(a, imp, dm));
        return new MethodSpecUser(baseMethod, ng);
    }

    static Instruction CloneInstr(Instruction ins)
    {
        return ins.Operand == null
            ? new Instruction(ins.OpCode)
            : new Instruction(ins.OpCode, ins.Operand);
    }

    static int Verify(string inDll)
    {
        var mod = ModuleDefMD.Load(inDll);
        bool ok = true;

        // 1) no AssemblyRef to ShinraRotationPatch
        bool hasExtRef = mod.GetAssemblyRefs().Any(a => a.Name == "ShinraRotationPatch");
        Console.WriteLine((hasExtRef ? "FAIL" : "OK  ") + " : external ShinraRotationPatch assembly ref " + (hasExtRef ? "STILL PRESENT" : "absent"));
        if (hasExtRef) ok = false;

        // 2) RotationEnricher is an internal TypeDef
        var rot = mod.Types.FirstOrDefault(t => t.FullName == "ShinraRotationPatch.RotationEnricher");
        Console.WriteLine((rot != null ? "OK  " : "FAIL") + " : RotationEnricher is a TypeDef inside this module " + (rot != null ? "yes" : "NO"));
        if (rot == null) ok = false;
        else
        {
            var enr = rot.Methods.FirstOrDefault(m => m.Name == "Enrich");
            Console.WriteLine((enr != null ? "OK  " : "FAIL") + " : Enrich method present " + (enr != null ? "yes" : "NO"));
            if (enr == null) ok = false;
        }

        // 3) Members.dealtSkillLog field present
        var members = mod.Types.FirstOrDefault(t => t.FullName == "DamageMeter.TeraDpsApi.Members");
        bool hasField = members != null && members.Fields.Any(f => f.Name == "dealtSkillLog");
        Console.WriteLine((hasField ? "OK  " : "FAIL") + " : Members.dealtSkillLog field present " + (hasField ? "yes" : "NO"));
        if (!hasField) ok = false;

        // 4) the injected call resolves to the internal MethodDef (not a MemberRef to external)
        var de = mod.Types.First(t => t.FullName == "DamageMeter.DataExporter");
        var ae = de.Methods.First(x => x.Name == "AutomatedExport" && x.Parameters.Count == 2
            && x.Parameters[0].Type.FullName == "Tera.Game.NpcEntity");
        var enrichCall = ae.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Call
            && i.Operand is IMethod im && im.Name == "Enrich");
        bool internalCall = enrichCall != null && enrichCall.Operand is MethodDef;
        Console.WriteLine((internalCall ? "OK  " : "FAIL") + " : injected Enrich call targets internal MethodDef " + (internalCall ? "yes" : "NO (still a MemberRef)"));
        if (!internalCall) ok = false;

        // 5) module-wide scan for ANY ref still scoped to the helper module
        Console.WriteLine();
        Console.WriteLine("ModuleRefs: " + string.Join(", ", mod.GetModuleRefs().Select(m2 => m2.Name.ToString())));
        foreach (var tr in mod.GetTypeRefs())
            if ((tr.Scope?.ScopeName ?? "").IndexOf("ShinraRotationPatch", StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine("  TypeRef  -> " + tr.FullName + "  [scope " + tr.Scope.ScopeName + "]");
        foreach (var mr in mod.GetMemberRefs())
            if ((mr.DeclaringType?.Scope?.ScopeName ?? "").IndexOf("ShinraRotationPatch", StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine("  MemberRef-> " + mr.FullName + "  [scope " + mr.DeclaringType.Scope.ScopeName + "]");
        if (rot != null)
        {
            var enr2 = rot.Methods.FirstOrDefault(m => m.Name == "Enrich");
            if (enr2?.Body != null)
            {
                foreach (var ins in enr2.Body.Instructions)
                {
                    string s = ins.Operand?.ToString() ?? "";
                    if (s.IndexOf("ShinraRotationPatch", StringComparison.OrdinalIgnoreCase) >= 0)
                        Console.WriteLine("  leftover ref @ " + ins.OpCode + " : " + s + "  [" + ins.Operand.GetType().Name + "]");
                    // also inspect the declaring scope of member refs
                    if (ins.Operand is IMemberRef mr2)
                    {
                        var scope = mr2.DeclaringType?.Scope?.ScopeName ?? "";
                        if (scope.IndexOf("ShinraRotationPatch", StringComparison.OrdinalIgnoreCase) >= 0)
                            Console.WriteLine("  member scope -> " + scope + " : " + mr2 + "  [" + ins.Operand.GetType().Name + "]");
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED");
        return ok ? 0 : 1;
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
