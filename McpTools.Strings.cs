using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MCP
{
    // String-literal search / reverse-lookup handlers. Lives as a partial so the MEF export +
    // dispatch switch stay in McpTools.cs; this file is just the two handlers + their helpers.
    //
    // Both tools scan `ldstr` operands in method bodies. For game/Unity reverse engineering the
    // business logic is wired together by string keys (PlayerPrefs keys, scene names, save-file
    // tokens), so "which method emits this string?" is the single most common question — and the
    // only way to answer it before this was ReadAllBytes + UTF-16 grep, which loses the method.
    sealed partial class McpTools
    {
        // ---------- search_string_literals ----------

        CallToolResult SearchStringLiterals(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("query", out var queryObj))
                throw new ArgumentException("query is required");

            var query = queryObj?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(query))
                throw new ArgumentException("query must not be empty");

            string? assemblyName = null;
            if (arguments.TryGetValue("assembly_name", out var asmObj) && asmObj != null)
            {
                assemblyName = asmObj.ToString();
                if (string.IsNullOrWhiteSpace(assemblyName))
                    assemblyName = null;
            }

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj?.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);
            var matches = BuildStringMatcher(query);

            // Restrict to one assembly if asked; otherwise sweep every loaded module.
            IEnumerable<ModuleDef> modules;
            if (assemblyName != null)
            {
                var assembly = FindAssemblyByName(assemblyName);
                if (assembly == null)
                    throw new ArgumentException($"Assembly not found: {assemblyName}");
                modules = assembly.Modules;
            }
            else
            {
                modules = documentTreeView.GetAllModuleNodes()
                    .Select(m => m.Document?.ModuleDef)
                    .Where(m => m != null)!
                    .Cast<ModuleDef>();
            }

            var results = new List<object>();
            foreach (var module in modules)
            {
                var moduleAsmName = module.Assembly?.Name.String ?? "Unknown";
                foreach (var type in module.GetTypes())
                {
                    foreach (var method in type.Methods)
                    {
                        foreach (var (index, il_offset, value) in EnumerateLdstr(method))
                        {
                            if (!matches(value))
                                continue;
                            results.Add(new
                            {
                                value,
                                assembly = moduleAsmName,
                                type = type.FullName,
                                method = method.Name.String,
                                method_token = method.MDToken.Raw,
                                signature = method.FullName,
                                il_index = index,
                                il_offset
                            });
                        }
                    }
                }
            }

            return CreatePaginatedResponse(results, offset, pageSize);
        }

        // ---------- list_string_constants ----------

        CallToolResult ListStringConstants(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;

            string? methodName = null;
            if (arguments.TryGetValue("method_name", out var mnObj) && mnObj != null)
            {
                methodName = mnObj.ToString();
                if (string.IsNullOrWhiteSpace(methodName))
                    methodName = null;
            }
            var parameterTypes = ReadStringArray(arguments, "parameter_types");
            var methodToken = ReadOptionalUInt(arguments, "method_token");

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj?.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");
            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            // Method scope: a single (possibly disambiguated) method. Type scope: the type and
            // all of its nested types — game logic is often buried in compiler-generated nested
            // closures, so a type-level listing that skips them would miss the interesting keys.
            IEnumerable<MethodDef> methods;
            if (methodName != null)
            {
                methods = new[] { FindMethod(type, methodName, parameterTypes, methodToken) };
            }
            else
            {
                methods = SelfAndNested(type).SelectMany(t => t.Methods);
            }

            var results = new List<object>();
            foreach (var method in methods)
            {
                foreach (var (index, il_offset, value) in EnumerateLdstr(method))
                {
                    results.Add(new
                    {
                        value,
                        type = method.DeclaringType?.FullName ?? type.FullName,
                        method = method.Name.String,
                        method_token = method.MDToken.Raw,
                        signature = method.FullName,
                        il_index = index,
                        il_offset
                    });
                }
            }

            return CreatePaginatedResponse(results, offset, pageSize);
        }

        // ---------- helpers ----------

        /// <summary>
        /// Extracts every <c>ldstr</c> (instruction index, IL offset, string value) from a method
        /// body. Methods without a body (abstract / extern / P/Invoke) and bodies that fail to
        /// parse (corrupt IL) yield nothing rather than throwing — a single bad method shouldn't
        /// abort a whole-assembly sweep.
        /// </summary>
        static IEnumerable<(int index, uint offset, string value)> EnumerateLdstr(MethodDef method)
        {
            if (!method.HasBody || method.Body == null)
                return Array.Empty<(int, uint, string)>();

            IList<Instruction> instrs;
            try
            {
                var body = method.Body;
                body.UpdateInstructionOffsets();
                instrs = body.Instructions;
            }
            catch
            {
                return Array.Empty<(int, uint, string)>();
            }

            var found = new List<(int, uint, string)>();
            for (int i = 0; i < instrs.Count; i++)
            {
                var ins = instrs[i];
                if (ins.OpCode.Code == Code.Ldstr && ins.Operand is string s)
                    found.Add((i, ins.Offset, s));
            }
            return found;
        }

        /// <summary>
        /// Builds a predicate matching a candidate string against the query. If the query contains
        /// a <c>*</c> it is treated as a wildcard anchored to the whole string (case-insensitive),
        /// mirroring search_types / get_type_fields; otherwise it is a case-insensitive substring
        /// match (the common "find SAVEFILE" case).
        /// </summary>
        static Func<string, bool> BuildStringMatcher(string query)
        {
            if (query.Contains("*"))
            {
                var pattern = "^" + Regex.Escape(query).Replace("\\*", ".*") + "$";
                var rx = new Regex(pattern, RegexOptions.IgnoreCase);
                return s => rx.IsMatch(s);
            }
            return s => s.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Yields a type and all of its nested types, recursively.</summary>
        static IEnumerable<TypeDef> SelfAndNested(TypeDef type)
        {
            yield return type;
            foreach (var nested in type.NestedTypes)
                foreach (var t in SelfAndNested(nested))
                    yield return t;
        }
    }
}
