using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MCP
{
    // Cross-reference handlers: find_callers (who invokes a method) and find_references (who
    // references a method / field / type / string). Lives as a partial so the MEF export +
    // dispatch switch stay in McpTools.cs.
    //
    // Xref is the decompiler's bread-and-butter and the only practical way to answer "who calls
    // PlayerPrefCheckAdd?", "who reads/writes field sceneToLoad?", "who uses this string key?"
    // without guessing class names and decompiling them one by one. Both tools sweep EVERY loaded
    // module — callers and references routinely live in a different assembly than the target, so a
    // single-assembly scan would miss the interesting cross-assembly edges.
    sealed partial class McpTools
    {
        // Opcodes that constitute "calling" a method. ldftn/ldvirtftn are included because a
        // delegate/function-pointer load is, in practice, a caller (the method is about to run).
        static readonly HashSet<Code> InvokeCodes = new HashSet<Code>
        {
            Code.Call, Code.Callvirt, Code.Newobj, Code.Ldftn, Code.Ldvirtftn,
        };

        // ---------- find_callers ----------

        CallToolResult FindCallers(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;
            var parameterTypes = ReadStringArray(arguments, "parameter_types");
            var methodToken = ReadOptionalUInt(arguments, "method_token");

            var (offset, pageSize) = DecodeCursor(ReadCursor(arguments));

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");
            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");
            var target = FindMethod(type, methodName, parameterTypes, methodToken);

            var results = ScanForReferences(ins =>
            {
                if (!InvokeCodes.Contains(ins.OpCode.Code))
                    return null;
                return ins.Operand is IMethod im && IsSameMethod(im, target) ? im.FullName : null;
            });

            return CreatePaginatedResponse(results, offset, pageSize);
        }

        // ---------- find_references ----------

        CallToolResult FindReferences(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("target_kind", out var kindObj))
                throw new ArgumentException("target_kind is required (one of: method, field, type, string)");
            var kind = (kindObj.ToString() ?? string.Empty).Trim().ToLowerInvariant();

            var (offset, pageSize) = DecodeCursor(ReadCursor(arguments));

            List<object> results;
            switch (kind)
            {
                case "method":
                {
                    var target = ResolveTargetMethod(arguments);
                    results = ScanForReferences(ins =>
                        ins.Operand is IMethod im && IsSameMethod(im, target) ? im.FullName : null);
                    break;
                }
                case "field":
                {
                    var target = ResolveTargetField(arguments);
                    results = ScanForReferences(ins =>
                        ins.Operand is IField f && IsSameField(f, target) ? f.FullName : null);
                    break;
                }
                case "type":
                {
                    var target = ResolveTargetType(arguments);
                    results = ScanForReferences(ins =>
                        ins.Operand is ITypeDefOrRef t && IsSameType(t, target) ? t.FullName : null);
                    break;
                }
                case "string":
                {
                    if (!arguments.TryGetValue("query", out var queryObj))
                        throw new ArgumentException("query is required when target_kind=string");
                    var query = queryObj?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(query))
                        throw new ArgumentException("query must not be empty");
                    var matches = BuildStringMatcher(query);
                    results = ScanForReferences(ins =>
                        ins.OpCode.Code == Code.Ldstr && ins.Operand is string s && matches(s) ? s : null);
                    break;
                }
                default:
                    throw new ArgumentException($"unknown target_kind '{kind}' (expected method, field, type, or string)");
            }

            return CreatePaginatedResponse(results, offset, pageSize);
        }

        // ---------- find_callees (dnSpy Analyze "Uses": outgoing references of ONE method) ----------
        //
        // The inverse of find_callers / find_references: instead of sweeping every module for who
        // references a target, this reads a single method's body and lists what IT references —
        // the methods it calls, the fields it reads/writes, and the types it touches. Results are
        // deduplicated per referenced member (one row, with the set of opcodes and a site count),
        // mirroring dnSpy's "Uses" node. The resolved MDToken on each row feeds decompile_by_token.

        CallToolResult FindCallees(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            var target = ResolveTargetMethod(arguments);
            var (offset, pageSize) = DecodeCursor(ReadCursor(arguments));

            var ordered = new List<string>();
            var agg = new Dictionary<string, CalleeAgg>(StringComparer.Ordinal);

            if (target.HasBody && target.Body != null)
            {
                IList<Instruction>? instrs = null;
                try
                {
                    target.Body.UpdateInstructionOffsets();
                    instrs = target.Body.Instructions;
                }
                catch { instrs = null; }

                if (instrs != null)
                {
                    for (int i = 0; i < instrs.Count; i++)
                    {
                        var ins = instrs[i];
                        var c = ClassifyOperand(ins.Operand);
                        if (c == null)
                            continue;
                        var (kind, signature, token, asm) = c.Value;
                        if (!agg.TryGetValue(signature, out var entry))
                        {
                            entry = new CalleeAgg(kind, signature, token, asm, i);
                            agg[signature] = entry;
                            ordered.Add(signature);
                        }
                        entry.Opcodes.Add(ins.OpCode.Name);
                        entry.Occurrences++;
                    }
                }
            }

            var results = ordered
                .Select(sig => agg[sig])
                .Select(e => (object)new
                {
                    ref_kind = e.RefKind,
                    signature = e.Signature,
                    token = e.Token,
                    target_assembly = e.TargetAssembly,
                    opcodes = e.Opcodes.OrderBy(o => o, StringComparer.Ordinal).ToList(),
                    occurrences = e.Occurrences,
                    first_il_index = e.FirstIlIndex,
                })
                .ToList();

            return CreatePaginatedResponse(results, offset, pageSize);
        }

        // ---------- find_overrides (dnSpy Analyze "Overridden By" / "Overrides") ----------
        //
        // direction=overridden_by (default): given a virtual/abstract method, find every subclass
        // that overrides it (the concrete bodies that actually run on a callvirt — find_callers only
        // finds the literal call site to the base slot, never these). direction=overrides: given a
        // method, find the base-class virtual(s) it overrides, walking the base chain.

        CallToolResult FindOverrides(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            var direction = (ReadOptionalString(arguments, "direction") ?? "overridden_by").Trim().ToLowerInvariant();
            var target = ResolveTargetMethod(arguments);
            var (offset, pageSize) = DecodeCursor(ReadCursor(arguments));

            var results = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (direction == "overridden_by")
            {
                var targetType = target.DeclaringType;
                if (targetType == null)
                    throw new ArgumentException("target method has no declaring type");
                foreach (var module in AllLoadedModules())
                {
                    var asm = module.Assembly?.Name.String ?? "Unknown";
                    foreach (var type in module.GetTypes())
                    {
                        if (SameTypeDef(type, targetType))
                            continue;
                        if (!InheritsFromType(type, targetType))
                            continue;
                        foreach (var m in type.Methods)
                        {
                            if (!m.IsVirtual || !OverridesTarget(m, target) || !seen.Add(m.FullName))
                                continue;
                            results.Add(new
                            {
                                type = type.FullName,
                                method = m.Name.String,
                                signature = m.FullName,
                                token = m.MDToken.Raw,
                                assembly = asm,
                                is_abstract = m.IsAbstract,
                            });
                        }
                    }
                }
            }
            else if (direction == "overrides")
            {
                var baseRef = target.DeclaringType?.BaseType;
                int guard = 0;
                while (baseRef != null && guard++ < 50)
                {
                    var bdef = TryResolve(() => baseRef.ResolveTypeDef());
                    if (bdef == null)
                        break;
                    foreach (var m in bdef.Methods)
                    {
                        if (!m.IsVirtual || !SameOverrideSignature(m, target) || !seen.Add(m.FullName))
                            continue;
                        results.Add(new
                        {
                            type = bdef.FullName,
                            method = m.Name.String,
                            signature = m.FullName,
                            token = m.MDToken.Raw,
                            assembly = bdef.Module?.Assembly?.Name.String ?? "Unknown",
                            is_abstract = m.IsAbstract,
                        });
                    }
                    baseRef = bdef.BaseType;
                }
                // Explicit overrides (e.g. explicit interface implementations) the signature walk misses.
                foreach (var mo in target.Overrides)
                {
                    var d = TryResolve(() => mo.MethodDeclaration?.ResolveMethodDef());
                    if (d == null || !seen.Add(d.FullName))
                        continue;
                    results.Add(new
                    {
                        type = d.DeclaringType?.FullName ?? "Unknown",
                        method = d.Name.String,
                        signature = d.FullName,
                        token = d.MDToken.Raw,
                        assembly = d.Module?.Assembly?.Name.String ?? "Unknown",
                        is_abstract = d.IsAbstract,
                    });
                }
            }
            else
            {
                throw new ArgumentException($"unknown direction '{direction}' (expected overridden_by or overrides)");
            }

            return CreatePaginatedResponse(results, offset, pageSize);
        }

        // Mutable accumulator so find_callees projects each referenced member once.
        sealed class CalleeAgg
        {
            public readonly string RefKind;
            public readonly string Signature;
            public readonly uint? Token;
            public readonly string? TargetAssembly;
            public readonly int FirstIlIndex;
            public int Occurrences;
            public readonly HashSet<string> Opcodes = new HashSet<string>(StringComparer.Ordinal);

            public CalleeAgg(string refKind, string signature, uint? token, string? targetAssembly, int firstIlIndex)
            {
                RefKind = refKind;
                Signature = signature;
                Token = token;
                TargetAssembly = targetAssembly;
                FirstIlIndex = firstIlIndex;
                Occurrences = 0;
            }
        }

        // Classifies an instruction operand into (ref_kind, signature, resolved token, owning assembly),
        // or null when the operand isn't a member/type reference (ints, strings, branch targets, calli).
        // Token/assembly come from resolving the ref to its Def; they stay null for refs into modules
        // that aren't loaded. Covers ldtoken (InlineTok), where the operand may be method, field, or type.
        static (string kind, string signature, uint? token, string? asm)? ClassifyOperand(object? operand)
        {
            switch (operand)
            {
                case MethodDef md:
                    return ("method", md.FullName, md.MDToken.Raw, md.Module?.Assembly?.Name.String);
                case FieldDef fd:
                    return ("field", fd.FullName, fd.MDToken.Raw, fd.Module?.Assembly?.Name.String);
                case TypeDef td:
                    return ("type", td.FullName, td.MDToken.Raw, td.Module?.Assembly?.Name.String);
                case MethodSpec msp:
                {
                    var d = TryResolve(() => msp.ResolveMethodDef());
                    return ("method", msp.FullName, d?.MDToken.Raw, d?.Module?.Assembly?.Name.String);
                }
                case MemberRef mr when mr.IsMethodRef:
                {
                    var d = TryResolve(() => mr.ResolveMethodDef());
                    return ("method", mr.FullName, d?.MDToken.Raw, d?.Module?.Assembly?.Name.String);
                }
                case MemberRef mr when mr.IsFieldRef:
                {
                    var d = TryResolve(() => mr.ResolveFieldDef());
                    return ("field", mr.FullName, d?.MDToken.Raw, d?.Module?.Assembly?.Name.String);
                }
                case TypeRef tr:
                {
                    var d = TryResolve(() => tr.ResolveTypeDef());
                    return ("type", tr.FullName, d?.MDToken.Raw, d?.Module?.Assembly?.Name.String);
                }
                case TypeSpec tsp:
                {
                    var d = TryResolve(() => tsp.ResolveTypeDef());
                    return ("type", tsp.FullName, d?.MDToken.Raw, d?.Module?.Assembly?.Name.String);
                }
                default:
                    return null;
            }
        }

        static T? TryResolve<T>(Func<T?> f) where T : class
        {
            try { return f(); }
            catch { return null; }
        }

        static bool SameTypeDef(TypeDef a, TypeDef b)
            => a == b || (a.MDToken.Raw == b.MDToken.Raw && a.Module == b.Module);

        static bool InheritsFromType(TypeDef type, TypeDef ancestor)
        {
            var cur = type.BaseType;
            int guard = 0;
            while (cur != null && guard++ < 50)
            {
                var def = TryResolve(() => cur.ResolveTypeDef());
                if (def == null)
                    break;
                if (SameTypeDef(def, ancestor))
                    return true;
                cur = def.BaseType;
            }
            return false;
        }

        static bool SameOverrideSignature(MethodDef a, MethodDef b)
            => a.Name == b.Name && a.MethodSig != null && b.MethodSig != null
               && new SigComparer().Equals(a.MethodSig, b.MethodSig);

        // A subclass method overrides target if it reuses the base vtable slot with a matching
        // name + signature, or explicitly lists target in its MethodOverrides.
        static bool OverridesTarget(MethodDef m, MethodDef target)
        {
            if (m.IsReuseSlot && SameOverrideSignature(m, target))
                return true;
            foreach (var mo in m.Overrides)
            {
                var d = TryResolve(() => mo.MethodDeclaration?.ResolveMethodDef());
                if (d != null && d == target)
                    return true;
            }
            return false;
        }

        // ---------- shared scan ----------

        /// <summary>
        /// Sweeps every instruction of every method body in every loaded module. <paramref name="probe"/>
        /// returns a non-null "reference description" string when the instruction references the target
        /// (otherwise null). Bodyless / corrupt methods are skipped so one bad method can't abort the
        /// whole-program sweep. Each match becomes a result row identifying the *caller* method plus the
        /// opcode and IL position of the reference.
        /// </summary>
        List<object> ScanForReferences(Func<Instruction, string?> probe)
        {
            var results = new List<object>();
            foreach (var module in AllLoadedModules())
            {
                var moduleAsmName = module.Assembly?.Name.String ?? "Unknown";
                foreach (var type in module.GetTypes())
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody || method.Body == null)
                            continue;
                        IList<Instruction> instrs;
                        try
                        {
                            var body = method.Body;
                            body.UpdateInstructionOffsets();
                            instrs = body.Instructions;
                        }
                        catch
                        {
                            continue;
                        }

                        for (int i = 0; i < instrs.Count; i++)
                        {
                            var ins = instrs[i];
                            var reference = probe(ins);
                            if (reference == null)
                                continue;
                            results.Add(new
                            {
                                caller_assembly = moduleAsmName,
                                caller_type = type.FullName,
                                caller_method = method.Name.String,
                                caller_token = method.MDToken.Raw,
                                signature = method.FullName,
                                opcode = ins.OpCode.Name,
                                reference,
                                il_index = i,
                                il_offset = (int)ins.Offset
                            });
                        }
                    }
                }
            }
            return results;
        }

        IEnumerable<ModuleDef> AllLoadedModules() =>
            documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.ModuleDef)
                .Where(m => m != null)
                .Cast<ModuleDef>();

        // ---------- target resolvers ----------

        MethodDef ResolveTargetMethod(Dictionary<string, object> arguments)
        {
            if (!arguments.TryGetValue("assembly_name", out var asm))
                throw new ArgumentException("assembly_name is required for target_kind=method");
            if (!arguments.TryGetValue("type_full_name", out var tfn))
                throw new ArgumentException("type_full_name is required for target_kind=method");
            if (!arguments.TryGetValue("method_name", out var mn))
                throw new ArgumentException("method_name is required for target_kind=method");
            var type = ResolveTargetTypeDef(asm.ToString() ?? string.Empty, tfn.ToString() ?? string.Empty);
            return FindMethod(type, mn.ToString() ?? string.Empty,
                ReadStringArray(arguments, "parameter_types"), ReadOptionalUInt(arguments, "method_token"));
        }

        FieldDef ResolveTargetField(Dictionary<string, object> arguments)
        {
            if (!arguments.TryGetValue("assembly_name", out var asm))
                throw new ArgumentException("assembly_name is required for target_kind=field");
            if (!arguments.TryGetValue("type_full_name", out var tfn))
                throw new ArgumentException("type_full_name is required for target_kind=field");
            if (!arguments.TryGetValue("field_name", out var fn))
                throw new ArgumentException("field_name is required for target_kind=field");
            var type = ResolveTargetTypeDef(asm.ToString() ?? string.Empty, tfn.ToString() ?? string.Empty);
            var fieldName = fn.ToString() ?? string.Empty;
            var field = type.Fields.FirstOrDefault(f => f.Name == fieldName);
            if (field == null)
                throw new ArgumentException($"Field not found: {type.FullName}::{fieldName}. Candidates: {string.Join(", ", type.Fields.Select(f => f.Name.String))}");
            return field;
        }

        TypeDef ResolveTargetType(Dictionary<string, object> arguments)
        {
            if (!arguments.TryGetValue("assembly_name", out var asm))
                throw new ArgumentException("assembly_name is required for target_kind=type");
            if (!arguments.TryGetValue("type_full_name", out var tfn))
                throw new ArgumentException("type_full_name is required for target_kind=type");
            return ResolveTargetTypeDef(asm.ToString() ?? string.Empty, tfn.ToString() ?? string.Empty);
        }

        TypeDef ResolveTargetTypeDef(string assemblyName, string typeFullName)
        {
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");
            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");
            return type;
        }

        // ---------- identity comparisons ----------
        //
        // Prefer resolving the operand to its Def and comparing by (module, MDToken) — this is exact
        // and handles MemberRef / MethodSpec / TypeSpec that the operand may actually be. Fall back to
        // FullName equality when resolution fails (e.g. a ref into an assembly that isn't loaded).

        static bool IsSameMethod(IMethod candidate, MethodDef target)
        {
            MethodDef? def = null;
            try { def = candidate.ResolveMethodDef(); } catch { /* unresolved */ }
            if (def != null)
                return def == target || (def.MDToken.Raw == target.MDToken.Raw && def.Module == target.Module);
            return string.Equals(candidate.FullName, target.FullName, StringComparison.Ordinal);
        }

        static bool IsSameField(IField candidate, FieldDef target)
        {
            FieldDef? def = null;
            try { def = candidate.ResolveFieldDef(); } catch { /* unresolved */ }
            if (def != null)
                return def == target || (def.MDToken.Raw == target.MDToken.Raw && def.Module == target.Module);
            return string.Equals(candidate.FullName, target.FullName, StringComparison.Ordinal);
        }

        static bool IsSameType(ITypeDefOrRef candidate, TypeDef target)
        {
            TypeDef? def = null;
            try { def = candidate.ResolveTypeDef(); } catch { /* unresolved */ }
            if (def != null)
                return def == target || (def.MDToken.Raw == target.MDToken.Raw && def.Module == target.Module);
            return string.Equals(candidate.FullName, target.FullName, StringComparison.Ordinal);
        }

        // Small shared arg helper (cursor is read the same way everywhere).
        static string? ReadCursor(Dictionary<string, object> arguments)
            => arguments.TryGetValue("cursor", out var c) ? c?.ToString() : null;
    }
}
