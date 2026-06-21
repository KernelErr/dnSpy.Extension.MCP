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
