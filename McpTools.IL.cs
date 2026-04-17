using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MCP
{
    // IL view / patch / save handlers. Lives as a partial so the MEF export + dispatch switch
    // stay in McpTools.cs; this file is just handlers + IL helpers.
    sealed partial class McpTools
    {
        // Snapshot store for revert_method_il. Populated lazily on first patch of a method.
        readonly Dictionary<uint, CilBodySnapshot> ilSnapshots = new Dictionary<uint, CilBodySnapshot>();

        // Serializes concurrent write ops (patch, revert, save) across HTTP clients. Reads do not
        // take this lock — they run on HTTP threads like every other read handler. Writes also
        // marshal through Application.Current.Dispatcher.Invoke to synchronize with any open
        // AsmEditor Edit-Method-Body dialog (which mutates Body.Instructions on the UI thread).
        readonly object ilEditLock = new object();

        /// <summary>
        /// Dispatches <paramref name="action"/> to the WPF UI thread if one exists, otherwise
        /// runs it inline. Used for any handler that mutates dnlib state (<c>Body.Instructions</c>
        /// lists, <c>ExceptionHandlers</c>, etc.) so we don't race AsmEditor's own UI-thread edits.
        /// </summary>
        T InvokeOnUiThread<T>(Func<T> action)
        {
            var app = Application.Current;
            var dispatcher = app?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess())
                return action();
            return dispatcher.Invoke(action);
        }

        // ---------- list_methods ----------

        CallToolResult ListMethods(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj?.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            // Marshal to the UI thread: IDocumentTreeView nodes are DispatcherObjects and
            // throw "calling thread cannot access this object" when touched from an HTTP thread
            // during / shortly after a tree mutation (e.g. the assembly we loaded via CLI arg).
            return InvokeOnUiThread(() =>
            {
                var assembly = FindAssemblyByName(assemblyName);
                if (assembly == null)
                    throw new ArgumentException($"Assembly not found: {assemblyName}");
                var type = FindTypeInAssembly(assembly, typeFullName);
                if (type == null)
                    throw new ArgumentException($"Type not found: {typeFullName}");

                var methods = type.Methods.Select(m => new
                {
                    name = m.Name.String,
                    token = m.MDToken.Raw,
                    signature = m.FullName,
                    return_type = m.ReturnType?.FullName ?? "void",
                    parameter_types = m.MethodSig == null
                        ? new List<string>()
                        : m.MethodSig.Params.Select(t => t?.FullName ?? "?").ToList(),
                    parameters = m.Parameters
                        .Where(p => !p.IsHiddenThisParameter)
                        .Select(p => new { name = p.Name, type = p.Type?.FullName ?? "?" })
                        .ToList(),
                    is_static = m.IsStatic,
                    is_virtual = m.IsVirtual,
                    is_abstract = m.IsAbstract,
                    has_body = m.HasBody
                }).ToList();

                return CreatePaginatedResponse(methods, offset, pageSize);
            });
        }

        // ---------- get_method_il ----------

        CallToolResult GetMethodIL(Dictionary<string, object>? arguments)
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

            return InvokeOnUiThread(() =>
            {
                var assembly = FindAssemblyByName(assemblyName);
                if (assembly == null)
                    throw new ArgumentException($"Assembly not found: {assemblyName}");
                var type = FindTypeInAssembly(assembly, typeFullName);
                if (type == null)
                    throw new ArgumentException($"Type not found: {typeFullName}");

                var method = FindMethod(type, methodName, parameterTypes, methodToken);
                if (!method.HasBody || method.Body == null)
                    throw new ArgumentException($"Method {DescribeSignature(method)} has no IL body (abstract / extern / P/Invoke).");

                var body = method.Body;
                body.UpdateInstructionOffsets();

                var result = SerializeBody(method, body);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return new CallToolResult
                {
                    Content = new List<ToolContent> {
                        new ToolContent { Text = json }
                    }
                };
            });
        }

        // ---------- patch_method_il ----------

        CallToolResult PatchMethodIL(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");
            if (!arguments.TryGetValue("edits", out var editsObj))
                throw new ArgumentException("edits is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;
            var parameterTypes = ReadStringArray(arguments, "parameter_types");
            var methodToken = ReadOptionalUInt(arguments, "method_token");
            var optimizeMacros = ReadOptionalBool(arguments, "optimize_macros") ?? false;

            var edits = ParseEditsList(editsObj);

            return InvokeOnUiThread(() =>
            {
                lock (ilEditLock)
                {
                    var assembly = FindAssemblyByName(assemblyName);
                    if (assembly == null)
                        throw new ArgumentException($"Assembly not found: {assemblyName}");
                    var type = FindTypeInAssembly(assembly, typeFullName);
                    if (type == null)
                        throw new ArgumentException($"Type not found: {typeFullName}");

                    var method = FindMethod(type, methodName, parameterTypes, methodToken);
                    if (!method.HasBody || method.Body == null)
                        throw new ArgumentException($"Method {DescribeSignature(method)} has no IL body.");
                    var body = method.Body;
                    var module = method.Module ?? assembly.ManifestModule;
                    if (module == null)
                        throw new ArgumentException("Cannot resolve owning module for method");

                    // Snapshot on first patch so revert_method_il can undo.
                    if (!ilSnapshots.ContainsKey(method.MDToken.Raw))
                        ilSnapshots[method.MDToken.Raw] = Snapshot(body);

                    // Resolve all edits against the pre-batch instruction list (by reference,
                    // so inserts/deletes don't shift resolutions).
                    var originalList = body.Instructions.ToList();
                    var importer = new Importer(module, ImporterOptions.TryToUseDefs);
                    var resolved = edits.Select(e => ResolveEdit(e, originalList, body, method, importer)).ToList();

                    // Apply in order. For replace, mutate the existing Instruction so branch/switch
                    // targets that reference it still resolve correctly.
                    foreach (var e in resolved)
                    {
                        switch (e.Op)
                        {
                            case "replace":
                                if (e.Target == null)
                                    throw new ArgumentException($"replace index {e.Index} out of range");
                                e.Target.OpCode = e.NewOpCode!;
                                e.Target.Operand = e.NewOperand;
                                break;
                            case "insert":
                                var newInstr = new Instruction(e.NewOpCode!) { Operand = e.NewOperand };
                                if (e.Target == null)
                                    body.Instructions.Add(newInstr);  // index == Count → append
                                else
                                {
                                    var idx = body.Instructions.IndexOf(e.Target);
                                    if (idx < 0)
                                        throw new ArgumentException("insert target no longer in body (was it deleted earlier in this batch?)");
                                    body.Instructions.Insert(idx, newInstr);
                                }
                                break;
                            case "delete":
                                if (e.Target == null || !body.Instructions.Remove(e.Target))
                                    throw new ArgumentException($"delete index {e.Index} out of range or already removed");
                                break;
                            case "set_init_locals":
                                body.InitLocals = e.BoolValue ?? body.InitLocals;
                                break;
                            default:
                                throw new ArgumentException($"Unknown edit op: {e.Op}");
                        }
                    }

                    if (optimizeMacros)
                        body.Instructions.OptimizeMacros();

                    body.UpdateInstructionOffsets();

                    var projection = SerializeBody(method, body);
                    projection["edits_applied"] = resolved.Count;
                    var json = JsonSerializer.Serialize(projection, new JsonSerializerOptions { WriteIndented = true });
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> {
                            new ToolContent { Text = json }
                        }
                    };
                }
            });
        }

        // ---------- revert_method_il ----------

        CallToolResult RevertMethodIL(Dictionary<string, object>? arguments)
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

            return InvokeOnUiThread(() =>
            {
                lock (ilEditLock)
                {
                    var assembly = FindAssemblyByName(assemblyName);
                    if (assembly == null)
                        throw new ArgumentException($"Assembly not found: {assemblyName}");
                    var type = FindTypeInAssembly(assembly, typeFullName);
                    if (type == null)
                        throw new ArgumentException($"Type not found: {typeFullName}");

                    var method = FindMethod(type, methodName, parameterTypes, methodToken);
                    if (!ilSnapshots.TryGetValue(method.MDToken.Raw, out var snap))
                        throw new ArgumentException($"No pending patch to revert for {DescribeSignature(method)}");
                    if (!method.HasBody || method.Body == null)
                        throw new ArgumentException($"Method {DescribeSignature(method)} has no IL body anymore.");
                    var body = method.Body;

                    body.Instructions.Clear();
                    foreach (var s in snap.Instructions)
                    {
                        // Un-mutate the Instruction object back to its pre-patch state — this
                        // preserves reference identity for branch/switch operands elsewhere in
                        // the body so targets that were pointing at this instruction still do.
                        s.Instr.OpCode = s.OpCode;
                        s.Instr.Operand = s.Operand;
                        body.Instructions.Add(s.Instr);
                    }
                    body.Variables.Clear();
                    foreach (var v in snap.Variables) body.Variables.Add(v);
                    body.ExceptionHandlers.Clear();
                    foreach (var eh in snap.ExceptionHandlers) body.ExceptionHandlers.Add(eh);
                    body.MaxStack = snap.MaxStack;
                    body.InitLocals = snap.InitLocals;
                    body.KeepOldMaxStack = snap.KeepOldMaxStack;
                    body.UpdateInstructionOffsets();

                    ilSnapshots.Remove(method.MDToken.Raw);

                    var projection = SerializeBody(method, body);
                    projection["reverted"] = true;
                    var json = JsonSerializer.Serialize(projection, new JsonSerializerOptions { WriteIndented = true });
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> {
                            new ToolContent { Text = json }
                        }
                    };
                }
            });
        }

        // ---------- snapshot helper ----------

        static CilBodySnapshot Snapshot(CilBody body) => new CilBodySnapshot
        {
            Instructions = body.Instructions.Select(i => new InstructionSnapshot
            {
                Instr = i,
                OpCode = i.OpCode,
                // For switch, the operand is an Instruction[] — clone it so a post-snapshot
                // in-place mutation of the array won't leak into the snapshot.
                Operand = i.Operand is Instruction[] arr ? (object)arr.ToArray() : i.Operand
            }).ToList(),
            Variables = body.Variables.ToList(),
            ExceptionHandlers = body.ExceptionHandlers.ToList(),
            MaxStack = body.MaxStack,
            InitLocals = body.InitLocals,
            KeepOldMaxStack = body.KeepOldMaxStack
        };

        // ---------- save_assembly ----------

        CallToolResult SaveAssembly(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;

            string? outputPath = null;
            if (arguments.TryGetValue("output_path", out var opObj) && opObj != null)
            {
                outputPath = opObj.ToString();
                if (string.IsNullOrWhiteSpace(outputPath))
                    outputPath = null;
            }

            return InvokeOnUiThread(() =>
            {
                lock (ilEditLock)
                {
                    var assembly = FindAssemblyByName(assemblyName);
                    if (assembly == null)
                        throw new ArgumentException($"Assembly not found: {assemblyName}");
                    var module = assembly.ManifestModule ?? assembly.Modules.FirstOrDefault();
                    if (module == null)
                        throw new ArgumentException($"Assembly {assemblyName} has no modules");

                    // Locate the loaded document for this module's filename so we can read
                    // the on-disk Location and disable its memory-mapped file handle.
                    var ownerDoc = documentTreeView.DocumentService.GetDocuments()
                        .FirstOrDefault(d => d.AssemblyDef == assembly)
                        ?? documentTreeView.DocumentService.GetDocuments()
                            .FirstOrDefault(d => d.ModuleDef == module);

                    var originalPath = ownerDoc?.Filename;
                    if (string.IsNullOrWhiteSpace(outputPath))
                    {
                        if (string.IsNullOrWhiteSpace(originalPath))
                            throw new ArgumentException($"Assembly {assemblyName} has no on-disk location; pass output_path explicitly.");
                        outputPath = originalPath;
                    }

                    outputPath = System.IO.Path.GetFullPath(outputPath!);

                    if (!string.IsNullOrEmpty(originalPath) && dnSpy.Contracts.Utilities.GacInfo.IsGacPath(originalPath!))
                        throw new ArgumentException("cannot save GAC assembly (refused by policy)");
                    if (dnSpy.Contracts.Utilities.GacInfo.IsGacPath(outputPath))
                        throw new ArgumentException("cannot save to a GAC path (refused by policy)");

                    // Backup-then-overwrite when we're writing to an existing file.
                    string? backupPath = null;
                    bool overwritingOriginal = !string.IsNullOrEmpty(originalPath) &&
                        string.Equals(System.IO.Path.GetFullPath(originalPath!), outputPath, StringComparison.OrdinalIgnoreCase);
                    if (overwritingOriginal && System.IO.File.Exists(outputPath))
                    {
                        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                        backupPath = outputPath + "." + stamp + ".bak";
                        System.IO.File.Copy(outputPath, backupPath, overwrite: false);
                        settings.Log($"Backed up {outputPath} → {System.IO.Path.GetFileName(backupPath)}");
                    }

                    // Disable memory-mapped I/O on every loaded document whose filename matches
                    // the target. Matches AsmEditor's MmapDisabler pattern — a single file may
                    // be referenced by more than one IDsDocument (main file, resources, etc.).
                    foreach (var doc in documentTreeView.DocumentService.GetDocuments())
                    {
                        if (string.IsNullOrEmpty(doc.Filename))
                            continue;
                        if (!string.Equals(doc.Filename, outputPath, StringComparison.OrdinalIgnoreCase))
                            continue;
                        (doc.PEImage as dnlib.PE.IInternalPEImage)?.UnsafeDisableMemoryMappedIO();
                    }

                    // Write. ModuleDefMD (loaded from disk) uses NativeWrite so native stubs,
                    // Win32 resources, delay-loaded imports and mixed-mode code survive.
                    // Freshly-constructed modules go through plain Write.
                    if (module is ModuleDefMD md)
                    {
                        var opts = new dnlib.DotNet.Writer.NativeModuleWriterOptions(md, optimizeImageSize: true);
                        opts.MetadataOptions.Flags |= dnlib.DotNet.Writer.MetadataFlags.RoslynSortInterfaceImpl;
                        md.NativeWrite(outputPath, opts);
                    }
                    else
                    {
                        var opts = new dnlib.DotNet.Writer.ModuleWriterOptions(module);
                        opts.MetadataOptions.Flags |= dnlib.DotNet.Writer.MetadataFlags.RoslynSortInterfaceImpl;
                        module.Write(outputPath, opts);
                    }

                    long bytes = 0;
                    try { bytes = new System.IO.FileInfo(outputPath).Length; } catch { /* ignore */ }

                    settings.Log($"save_assembly: {assemblyName} → {outputPath} ({bytes} bytes)" + (backupPath != null ? $", backup {System.IO.Path.GetFileName(backupPath)}" : ""));

                    var result = new Dictionary<string, object?>
                    {
                        ["saved_to"] = outputPath,
                        ["bytes_written"] = bytes,
                        ["backup_path"] = backupPath,
                        ["note"] = "dnSpy's in-memory view is NOT refreshed. Reopen the assembly in dnSpy to see the saved state."
                    };
                    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> {
                            new ToolContent { Text = json }
                        }
                    };
                }
            });
        }

        // ---------- edit parsing + resolving ----------

        sealed class EditOp
        {
            public string Op { get; set; } = string.Empty;
            public int Index { get; set; }
            public string? OpCodeName { get; set; }
            public string? OperandRaw { get; set; }
            public bool? BoolValue { get; set; }
        }

        sealed class ResolvedEdit
        {
            public string Op { get; set; } = string.Empty;
            public int Index { get; set; }
            public Instruction? Target { get; set; }
            public OpCode? NewOpCode { get; set; }
            public object? NewOperand { get; set; }
            public bool? BoolValue { get; set; }
        }

        static List<EditOp> ParseEditsList(object? raw)
        {
            if (raw == null)
                throw new ArgumentException("edits must be an array");

            var list = new List<EditOp>();

            if (raw is JsonElement el)
            {
                if (el.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("edits must be a JSON array");
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("each edit must be an object");
                    list.Add(ParseEditObject(item));
                }
                return list;
            }

            if (raw is IEnumerable<object> seq)
            {
                foreach (var item in seq)
                {
                    if (item is JsonElement je)
                        list.Add(ParseEditObject(je));
                    else if (item is Dictionary<string, object> dict)
                        list.Add(ParseEditDict(dict));
                    else
                        throw new ArgumentException($"unsupported edit shape: {item?.GetType().Name}");
                }
                return list;
            }

            throw new ArgumentException("edits must be an array of objects");
        }

        static EditOp ParseEditObject(JsonElement obj)
        {
            var e = new EditOp();
            if (!obj.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String)
                throw new ArgumentException("edit.op (string) is required");
            e.Op = opEl.GetString() ?? string.Empty;

            if (obj.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number && idxEl.TryGetInt32(out var idx))
                e.Index = idx;
            if (obj.TryGetProperty("opcode", out var oc) && oc.ValueKind == JsonValueKind.String)
                e.OpCodeName = oc.GetString();
            if (obj.TryGetProperty("operand", out var op))
                e.OperandRaw = op.ValueKind == JsonValueKind.String ? op.GetString() : op.ToString();
            if (obj.TryGetProperty("value", out var v))
            {
                if (v.ValueKind == JsonValueKind.True) e.BoolValue = true;
                else if (v.ValueKind == JsonValueKind.False) e.BoolValue = false;
            }
            return e;
        }

        static EditOp ParseEditDict(Dictionary<string, object> dict)
        {
            var e = new EditOp();
            if (!dict.TryGetValue("op", out var opObj) || opObj == null)
                throw new ArgumentException("edit.op is required");
            e.Op = opObj.ToString() ?? string.Empty;
            if (dict.TryGetValue("index", out var idxObj) && idxObj != null)
                e.Index = Convert.ToInt32(idxObj, CultureInfo.InvariantCulture);
            if (dict.TryGetValue("opcode", out var ocObj) && ocObj != null)
                e.OpCodeName = ocObj.ToString();
            if (dict.TryGetValue("operand", out var opdObj) && opdObj != null)
                e.OperandRaw = opdObj.ToString();
            if (dict.TryGetValue("value", out var vObj) && vObj != null)
            {
                if (vObj is bool b) e.BoolValue = b;
                else if (bool.TryParse(vObj.ToString(), out var pb)) e.BoolValue = pb;
            }
            return e;
        }

        ResolvedEdit ResolveEdit(EditOp e, IList<Instruction> originalList, CilBody body, MethodDef method, Importer importer)
        {
            var r = new ResolvedEdit { Op = e.Op, Index = e.Index, BoolValue = e.BoolValue };
            switch (e.Op)
            {
                case "replace":
                case "delete":
                    if (e.Index < 0 || e.Index >= originalList.Count)
                        throw new ArgumentException($"{e.Op} index {e.Index} out of range [0,{originalList.Count})");
                    r.Target = originalList[e.Index];
                    if (e.Op == "replace")
                    {
                        if (string.IsNullOrEmpty(e.OpCodeName))
                            throw new ArgumentException("replace requires opcode");
                        r.NewOpCode = ParseOpCode(e.OpCodeName!);
                        r.NewOperand = ParseOperand(e.OperandRaw ?? string.Empty, r.NewOpCode!, originalList, body, method, importer);
                    }
                    return r;
                case "insert":
                    if (e.Index < 0 || e.Index > originalList.Count)
                        throw new ArgumentException($"insert index {e.Index} out of range [0,{originalList.Count}]");
                    r.Target = e.Index < originalList.Count ? originalList[e.Index] : null; // null = append
                    if (string.IsNullOrEmpty(e.OpCodeName))
                        throw new ArgumentException("insert requires opcode");
                    r.NewOpCode = ParseOpCode(e.OpCodeName!);
                    r.NewOperand = ParseOperand(e.OperandRaw ?? string.Empty, r.NewOpCode!, originalList, body, method, importer);
                    return r;
                case "set_init_locals":
                    if (!e.BoolValue.HasValue)
                        throw new ArgumentException("set_init_locals requires value (bool)");
                    return r;
                default:
                    throw new ArgumentException($"Unknown edit op: {e.Op}");
            }
        }

        // ---------- opcode parser ----------

        static Dictionary<string, OpCode>? opCodeByName;
        static readonly object opCodeByNameLock = new object();

        static OpCode ParseOpCode(string name)
        {
            if (opCodeByName == null)
            {
                lock (opCodeByNameLock)
                {
                    if (opCodeByName == null)
                    {
                        var map = new Dictionary<string, OpCode>(StringComparer.OrdinalIgnoreCase);
                        foreach (var f in typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                        {
                            if (f.FieldType != typeof(OpCode))
                                continue;
                            var oc = (OpCode)f.GetValue(null)!;
                            map[oc.Name] = oc;
                        }
                        opCodeByName = map;
                    }
                }
            }
            if (opCodeByName!.TryGetValue(name, out var result))
                return result;
            throw new ArgumentException($"Unknown opcode: '{name}'. Use dnlib names like 'ldarg.0', 'ldc.i4.s', 'brtrue.s', 'callvirt'.");
        }

        // ---------- operand parser ----------

        object? ParseOperand(string operandRaw, OpCode opcode, IList<Instruction> originalList, CilBody body, MethodDef method, Importer importer)
        {
            var ot = opcode.OperandType;
            if (ot == OperandType.InlineNone)
                return null;

            var s = operandRaw ?? string.Empty;
            var colon = s.IndexOf(':');
            if (colon < 0)
                throw new ArgumentException($"operand '{s}' for {opcode.Name} must be tagged (e.g. 'int:1', 'str:\"x\"', 'label:3'). See the patch_method_il doc.");
            var tag = s.Substring(0, colon);
            var rest = s.Substring(colon + 1);

            switch (ot)
            {
                case OperandType.InlineI:
                    ExpectTag(tag, opcode, "int");
                    return int.Parse(rest, CultureInfo.InvariantCulture);
                case OperandType.ShortInlineI:
                    if (tag == "int8") return sbyte.Parse(rest, CultureInfo.InvariantCulture);
                    if (tag == "uint8") return byte.Parse(rest, CultureInfo.InvariantCulture);
                    throw new ArgumentException($"operand for {opcode.Name} needs 'int8:' or 'uint8:' tag, got '{tag}:'");
                case OperandType.InlineI8:
                    ExpectTag(tag, opcode, "long");
                    return long.Parse(rest, CultureInfo.InvariantCulture);
                case OperandType.ShortInlineR:
                    ExpectTag(tag, opcode, "float");
                    return float.Parse(rest, CultureInfo.InvariantCulture);
                case OperandType.InlineR:
                    ExpectTag(tag, opcode, "double");
                    return double.Parse(rest, CultureInfo.InvariantCulture);
                case OperandType.InlineString:
                    ExpectTag(tag, opcode, "str");
                    // rest is a JSON-quoted string literal, e.g. "hello\n"
                    try
                    {
                        using var doc = JsonDocument.Parse(rest);
                        if (doc.RootElement.ValueKind != JsonValueKind.String)
                            throw new ArgumentException("str: operand must be a JSON-quoted string");
                        return doc.RootElement.GetString() ?? string.Empty;
                    }
                    catch (JsonException ex)
                    {
                        throw new ArgumentException($"Invalid JSON string in str: operand: {ex.Message}");
                    }
                case OperandType.InlineMethod:
                    ExpectTag(tag, opcode, "method");
                    return ResolveMethodRef(rest, importer);
                case OperandType.InlineField:
                    ExpectTag(tag, opcode, "field");
                    return ResolveFieldRef(rest, importer);
                case OperandType.InlineType:
                    ExpectTag(tag, opcode, "type");
                    return ResolveTypeRef(rest, importer);
                case OperandType.InlineTok:
                    ExpectTag(tag, opcode, "token");
                    // rest is e.g. "method:...", "field:...", "type:..."
                    var tokColon = rest.IndexOf(':');
                    if (tokColon < 0)
                        throw new ArgumentException("token operand must be 'token:method:...', 'token:field:...', or 'token:type:...'");
                    var innerTag = rest.Substring(0, tokColon);
                    var innerRest = rest.Substring(tokColon + 1);
                    return innerTag switch
                    {
                        "method" => (object)ResolveMethodRef(innerRest, importer),
                        "field" => ResolveFieldRef(innerRest, importer),
                        "type" => ResolveTypeRef(innerRest, importer),
                        _ => throw new ArgumentException($"unknown token inner tag '{innerTag}'")
                    };
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineBrTarget:
                    ExpectTag(tag, opcode, "label");
                    {
                        var idx = int.Parse(rest, CultureInfo.InvariantCulture);
                        if (idx < 0 || idx >= originalList.Count)
                            throw new ArgumentException($"label:{idx} out of range [0,{originalList.Count})");
                        return originalList[idx];
                    }
                case OperandType.InlineSwitch:
                    ExpectTag(tag, opcode, "switch");
                    if (!rest.StartsWith("[") || !rest.EndsWith("]"))
                        throw new ArgumentException("switch operand must be 'switch:[<i>,<i>,...]'");
                    var inner = rest.Substring(1, rest.Length - 2);
                    if (string.IsNullOrWhiteSpace(inner))
                        return Array.Empty<Instruction>();
                    return inner.Split(',').Select(part =>
                    {
                        var idx = int.Parse(part.Trim(), CultureInfo.InvariantCulture);
                        if (idx < 0 || idx >= originalList.Count)
                            throw new ArgumentException($"switch label {idx} out of range [0,{originalList.Count})");
                        return originalList[idx];
                    }).ToArray();
                case OperandType.InlineVar:
                case OperandType.ShortInlineVar:
                    if (tag == "local")
                    {
                        var idx = int.Parse(rest, CultureInfo.InvariantCulture);
                        if (idx < 0 || idx >= body.Variables.Count)
                            throw new ArgumentException($"local:{idx} out of range [0,{body.Variables.Count})");
                        return body.Variables[idx];
                    }
                    if (tag == "arg")
                    {
                        var idx = int.Parse(rest, CultureInfo.InvariantCulture);
                        if (idx < 0 || idx >= method.Parameters.Count)
                            throw new ArgumentException($"arg:{idx} out of range [0,{method.Parameters.Count})");
                        return method.Parameters[idx];
                    }
                    throw new ArgumentException($"operand for {opcode.Name} needs 'local:' or 'arg:', got '{tag}:'");
                case OperandType.InlineSig:
                    throw new ArgumentException("calli / InlineSig operands are not supported in this release");
                default:
                    throw new ArgumentException($"operand type {ot} not supported in this release");
            }
        }

        static void ExpectTag(string actual, OpCode opcode, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new ArgumentException($"operand for {opcode.Name} needs '{expected}:' tag, got '{actual}:'");
        }

        // ---------- member reference resolvers ----------

        IMethod ResolveMethodRef(string fullName, Importer importer)
        {
            var needle = fullName.Trim();
            foreach (var mnode in documentTreeView.GetAllModuleNodes())
            {
                var mod = mnode.Document?.ModuleDef;
                if (mod == null) continue;
                foreach (var t in mod.GetTypes())
                {
                    foreach (var m in t.Methods)
                    {
                        if (string.Equals(m.FullName, needle, StringComparison.Ordinal))
                            return importer.Import(m);
                    }
                }
            }
            throw new ArgumentException($"No method with FullName '{needle}' in any loaded assembly. Copy the 'signature' field from list_methods.");
        }

        IField ResolveFieldRef(string fullName, Importer importer)
        {
            var needle = fullName.Trim();
            foreach (var mnode in documentTreeView.GetAllModuleNodes())
            {
                var mod = mnode.Document?.ModuleDef;
                if (mod == null) continue;
                foreach (var t in mod.GetTypes())
                {
                    foreach (var f in t.Fields)
                    {
                        if (string.Equals(f.FullName, needle, StringComparison.Ordinal))
                            return importer.Import(f);
                    }
                }
            }
            throw new ArgumentException($"No field with FullName '{needle}' in any loaded assembly. Copy from get_type_info.Fields[].");
        }

        ITypeDefOrRef ResolveTypeRef(string fullName, Importer importer)
        {
            var needle = fullName.Trim();
            foreach (var mnode in documentTreeView.GetAllModuleNodes())
            {
                var mod = mnode.Document?.ModuleDef;
                if (mod == null) continue;
                foreach (var t in mod.GetTypes())
                {
                    if (string.Equals(t.FullName, needle, StringComparison.Ordinal))
                        return importer.Import(t);
                }
            }
            throw new ArgumentException($"No type with FullName '{needle}' in any loaded assembly.");
        }

        // ---------- small helper added here so both phases can reach it ----------

        static bool? ReadOptionalBool(Dictionary<string, object> args, string key)
        {
            if (!args.TryGetValue(key, out var raw) || raw == null)
                return null;
            if (raw is bool b) return b;
            if (raw is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
                if (el.ValueKind == JsonValueKind.Null) return null;
                if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var pb)) return pb;
            }
            if (bool.TryParse(raw.ToString(), out var rp)) return rp;
            throw new ArgumentException($"{key} must be a boolean");
        }

        // ---------- shared body → JSON projection ----------

        Dictionary<string, object?> SerializeBody(MethodDef method, CilBody body)
        {
            var instrs = body.Instructions;
            var indexMap = new Dictionary<Instruction, int>(instrs.Count);
            for (int i = 0; i < instrs.Count; i++)
                indexMap[instrs[i]] = i;

            var rendered = new List<object>(instrs.Count);
            for (int i = 0; i < instrs.Count; i++)
            {
                var ins = instrs[i];
                rendered.Add(new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["offset"] = (int)ins.Offset,
                    ["opcode"] = ins.OpCode.Name,
                    ["operand"] = RenderOperand(ins, indexMap)
                });
            }

            var locals = body.Variables.Select((v, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["type"] = v.Type?.FullName ?? "?",
                ["name"] = string.IsNullOrEmpty(v.Name) ? null : v.Name
            }).ToList();

            var handlers = body.ExceptionHandlers.Select(eh => new Dictionary<string, object?>
            {
                ["handler_type"] = eh.HandlerType.ToString(),
                ["try_start"] = IndexOrEnd(eh.TryStart, indexMap, instrs.Count),
                ["try_end"] = IndexOrEnd(eh.TryEnd, indexMap, instrs.Count),
                ["handler_start"] = IndexOrEnd(eh.HandlerStart, indexMap, instrs.Count),
                ["handler_end"] = IndexOrEnd(eh.HandlerEnd, indexMap, instrs.Count),
                ["filter_start"] = IndexOrEnd(eh.FilterStart, indexMap, instrs.Count),
                ["catch_type"] = eh.CatchType?.FullName
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["method"] = new Dictionary<string, object?>
                {
                    ["name"] = method.Name.String,
                    ["token"] = method.MDToken.Raw,
                    ["signature"] = method.FullName
                },
                ["instructions"] = rendered,
                ["max_stack"] = (int)body.MaxStack,
                ["init_locals"] = body.InitLocals,
                ["keep_old_max_stack"] = body.KeepOldMaxStack,
                ["local_var_sig_tok"] = body.LocalVarSigTok,
                ["locals"] = locals,
                ["exception_handlers"] = handlers,
                ["has_pending_patch"] = ilSnapshots.ContainsKey(method.MDToken.Raw)
            };
        }

        static int? IndexOrEnd(Instruction? target, Dictionary<Instruction, int> indexMap, int count)
        {
            if (target == null)
                return null;
            return indexMap.TryGetValue(target, out var idx) ? idx : count;
        }

        // ---------- operand renderer ----------

        /// <summary>
        /// Serializes an instruction operand into the tagged grammar described in the feature plan.
        /// Mirrored by <c>ParseOperand</c> (added in the patch_method_il step) for round-tripping.
        /// </summary>
        static string RenderOperand(Instruction ins, Dictionary<Instruction, int> indexMap)
        {
            var op = ins.Operand;
            switch (ins.OpCode.OperandType)
            {
                case OperandType.InlineNone:
                    return string.Empty;
                case OperandType.InlineI:
                    return op == null ? "int:0" : "int:" + Convert.ToInt32(op, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                case OperandType.ShortInlineI:
                    // Operand is sbyte for ldc.i4.s; byte for unaligned. prefix, volatile. etc. use inline:
                    return op == null ? "int8:0" : (op is byte b
                        ? "uint8:" + b.ToString(CultureInfo.InvariantCulture)
                        : "int8:" + Convert.ToSByte(op, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                case OperandType.InlineI8:
                    return op == null ? "long:0" : "long:" + Convert.ToInt64(op, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                case OperandType.ShortInlineR:
                    return op == null ? "float:0" : "float:" + Convert.ToSingle(op, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                case OperandType.InlineR:
                    return op == null ? "double:0" : "double:" + Convert.ToDouble(op, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                case OperandType.InlineString:
                    return "str:" + JsonSerializer.Serialize(op as string ?? string.Empty);
                case OperandType.InlineMethod:
                    return op == null ? "method:?" : "method:" + ((IMethod)op).FullName;
                case OperandType.InlineField:
                    return op == null ? "field:?" : "field:" + ((IField)op).FullName;
                case OperandType.InlineType:
                    return op == null ? "type:?" : "type:" + ((ITypeDefOrRef)op).FullName;
                case OperandType.InlineTok:
                    return op switch
                    {
                        IMethod m => "token:method:" + m.FullName,
                        IField f => "token:field:" + f.FullName,
                        ITypeDefOrRef t => "token:type:" + t.FullName,
                        null => "token:?",
                        _ => "token:?:" + op
                    };
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineBrTarget:
                    if (op is Instruction target && indexMap.TryGetValue(target, out var tidx))
                        return "label:" + tidx.ToString(CultureInfo.InvariantCulture);
                    return "label:-1";
                case OperandType.InlineSwitch:
                    if (op is IList<Instruction> targets)
                    {
                        var ids = targets.Select(t => t != null && indexMap.TryGetValue(t, out var x) ? x : -1);
                        return "switch:[" + string.Join(",", ids) + "]";
                    }
                    if (op is Instruction[] arr)
                    {
                        var ids = arr.Select(t => t != null && indexMap.TryGetValue(t, out var x) ? x : -1);
                        return "switch:[" + string.Join(",", ids) + "]";
                    }
                    return "switch:[]";
                case OperandType.InlineVar:
                case OperandType.ShortInlineVar:
                    if (op is Local local)
                        return "local:" + local.Index.ToString(CultureInfo.InvariantCulture);
                    if (op is Parameter parameter)
                        return "arg:" + parameter.Index.ToString(CultureInfo.InvariantCulture);
                    return op?.ToString() ?? "?";
                case OperandType.InlineSig:
                    return "sig:" + (op?.ToString() ?? "?");
                case OperandType.InlinePhi:
                    return "phi:" + (op?.ToString() ?? "?");
                default:
                    return op?.ToString() ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Pre-patch snapshot of a CilBody used by revert_method_il. Captures both the ordering of
    /// Instruction references AND each instruction's (OpCode, Operand) — the replace path
    /// mutates Instruction objects in place so we can preserve branch-target identity, and
    /// that mutation would otherwise poison a reference-only snapshot.
    /// </summary>
    sealed class CilBodySnapshot
    {
        public List<InstructionSnapshot> Instructions { get; set; } = new List<InstructionSnapshot>();
        public List<Local> Variables { get; set; } = new List<Local>();
        public List<ExceptionHandler> ExceptionHandlers { get; set; } = new List<ExceptionHandler>();
        public ushort MaxStack { get; set; }
        public bool InitLocals { get; set; }
        public bool KeepOldMaxStack { get; set; }
    }

    sealed class InstructionSnapshot
    {
        public Instruction Instr { get; set; } = null!;
        public OpCode OpCode { get; set; } = null!;
        public object? Operand { get; set; }
    }
}
