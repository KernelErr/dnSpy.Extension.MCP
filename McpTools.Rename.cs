using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP
{
    sealed partial class McpTools
    {
        const SigComparerOptions RenameTypeComparerOptions =
            SigComparerOptions.CompareDeclaringTypes |
            SigComparerOptions.CompareAssemblyPublicKeyToken |
            SigComparerOptions.TypeRefCanReferenceGlobalType |
            SigComparerOptions.PrivateScopeIsComparable |
            SigComparerOptions.DontProjectWinMDRefs;
        const SigComparerOptions RenameMemberComparerOptions =
            SigComparerOptions.CompareAssemblyPublicKeyToken |
            SigComparerOptions.TypeRefCanReferenceGlobalType |
            SigComparerOptions.PrivateScopeIsComparable |
            SigComparerOptions.DontProjectWinMDRefs;

        CallToolResult RenameMethodSymbol(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");

            var token = ReadOptionalUInt(arguments, "token")
                ?? throw new ArgumentException("token is required (MethodDef MDToken.Raw — decimal uint or '0x' hex string)");
            if ((token & 0xFF000000U) != 0x06000000U)
                throw new ArgumentException($"Token 0x{token:X8} is not a MethodDef token (expected table prefix 0x06).");

            var newName = ReadOptionalString(arguments, "new_name")
                ?? throw new ArgumentException("new_name is required and cannot be empty");
            newName = newName.Trim();
            if (newName.IndexOf('\0') >= 0)
                throw new ArgumentException("new_name cannot contain a NUL character");

            var assemblyName = ReadOptionalString(arguments, "assembly_name");
            var (module, method) = ResolveMethodByToken(token, assemblyName);
            if (method.IsConstructor)
                throw new ArgumentException(
                    $"Constructor {method.FullName} cannot be renamed; .ctor and .cctor names are reserved by the CLR.");

            var oldName = method.Name.String;
            var oldFullName = method.FullName;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return CreateRenameMethodResult(
                    module, method, token, oldName, oldFullName, 0, changed: false);

            var signatureComparer = new SigComparer(RenameMemberComparerOptions);
            var duplicate = method.DeclaringType.Methods.FirstOrDefault(m =>
                m != method &&
                string.Equals(m.Name.String, newName, StringComparison.Ordinal) &&
                signatureComparer.Equals(m.MethodSig, method.MethodSig));
            if (duplicate != null)
                throw new ArgumentException(
                    $"Method {method.DeclaringType.FullName} already contains '{newName}' with the same signature " +
                    $"(0x{duplicate.MDToken.Raw:X8}).");

            // MethodDef operands update automatically because they point at this object. Imported calls
            // use MemberRef rows, so update the matching rows in the declaring module as dnSpy's editor
            // does when it renames a method.
            var memberRefs = EnumerateMethodMemberRefs(module)
                .Where(m => m.IsMethodRef && MemberRefTargetsMethod(m, method))
                .Select(m => (memberRef: m, oldName: m.Name))
                .ToList();

            var newUtf8Name = new UTF8String(newName);
            var methodNode = documentTreeView.FindNode(method);
            var parentNode = methodNode?.TreeNode.Parent;
            var originalIndex = parentNode == null || methodNode == null
                ? -1
                : parentNode.Children.IndexOf(methodNode.TreeNode);
            var wasSelected = methodNode != null && methodNode.TreeNode.TreeView.SelectedItem == methodNode;

            try
            {
                // Reinsert a visible node so the declaring type's method sort order is recalculated.
                if (parentNode != null && methodNode != null && originalIndex >= 0)
                    parentNode.Children.RemoveAt(originalIndex);

                method.Name = newUtf8Name;
                foreach (var (memberRef, _) in memberRefs)
                    memberRef.Name = newUtf8Name;

                if (parentNode != null && methodNode != null && originalIndex >= 0)
                    parentNode.AddChild(methodNode.TreeNode);
            }
            catch
            {
                method.Name = new UTF8String(oldName);
                foreach (var (memberRef, memberRefOldName) in memberRefs)
                    memberRef.Name = memberRefOldName;
                if (parentNode != null && methodNode != null && originalIndex >= 0)
                {
                    var currentIndex = parentNode.Children.IndexOf(methodNode.TreeNode);
                    if (currentIndex >= 0)
                        parentNode.Children.RemoveAt(currentIndex);
                    parentNode.Children.Insert(Math.Min(originalIndex, parentNode.Children.Count), methodNode.TreeNode);
                }
                throw;
            }

            try
            {
                if (wasSelected && methodNode != null)
                    methodNode.TreeNode.TreeView.SelectItems(new[] { methodNode });
                methodNode?.TreeNode.RefreshUI();
            }
            catch (Exception ex)
            {
                settings.Log($"rename_symbol_by_token[method] UI refresh warning: {ex.Message}");
            }
            RefreshDecompiledViews(module, "rename_symbol_by_token");

            var updatedMemberReferences = memberRefs
                .Select(r => r.memberRef.MDToken.Raw)
                .Where(memberRefToken => memberRefToken != 0)
                .Distinct()
                .Count();
            if (updatedMemberReferences == 0 && memberRefs.Count != 0)
                updatedMemberReferences = memberRefs.Count;
            settings.Log(
                $"rename_symbol_by_token[method]: {module.Assembly?.Name.String ?? module.Name.String} " +
                $"0x{token:X8} {oldFullName} → {method.FullName} ({updatedMemberReferences} MemberRefs updated)");

            return CreateRenameMethodResult(
                module, method, token, oldName, oldFullName, updatedMemberReferences, changed: true);
        }

        CallToolResult RenameClassOrEnumSymbolCore(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");

            var token = ReadOptionalUInt(arguments, "token")
                ?? throw new ArgumentException("token is required (TypeDef MDToken.Raw — decimal uint or '0x' hex string)");
            if ((token & 0xFF000000U) != 0x02000000U)
                throw new ArgumentException($"Token 0x{token:X8} is not a TypeDef token (expected table prefix 0x02).");

            var newName = ReadOptionalString(arguments, "new_name")
                ?? throw new ArgumentException("new_name is required and cannot be empty");
            newName = newName.Trim();
            if (newName.IndexOf('\0') >= 0)
                throw new ArgumentException("new_name cannot contain a NUL character");

            var assemblyName = ReadOptionalString(arguments, "assembly_name");
            var (module, type) = ResolveTypeByToken(token, assemblyName);

            if (type.IsGlobalModuleType)
                throw new ArgumentException("The special <Module> type cannot be renamed.");
            if (!type.IsEnum && (type.IsInterface || type.IsValueType))
                throw new ArgumentException(
                    $"Type {type.FullName} is neither a class nor an enum. This tool does not rename interfaces or structs.");

            var oldName = type.Name.String;
            var oldFullName = type.FullName;
            var typeKind = type.IsEnum ? "enum" : "class";

            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return CreateRenameTypeResult(module, type, token, typeKind, oldName, oldFullName, 0, changed: false);

            var duplicate = type.DeclaringType != null
                ? type.DeclaringType.NestedTypes.Any(t => t != type && t.Name.String == newName)
                : module.Types.Any(t => t != type && t.Namespace.String == type.Namespace.String && t.Name.String == newName);
            if (duplicate)
                throw new ArgumentException(
                    $"A sibling type named '{newName}' already exists in {(type.DeclaringType?.FullName ?? type.Namespace.String)}.");

            // Match dnSpy's own Edit Type command: update TypeRefs in the declaring module before
            // changing the TypeDef name. Without this, member signatures backed by TypeRef rows can
            // keep rendering the old name after the definition itself has been renamed.
            var comparer = new TypeEqualityComparer(RenameTypeComparerOptions);
            var typeRefs = module.GetTypeRefs()
                .Where(t => comparer.Equals(t, type))
                .ToArray();

            var newUtf8Name = new UTF8String(newName);
            var typeNode = documentTreeView.FindNode(type);
            var parentNode = typeNode?.TreeNode.Parent;
            var originalIndex = parentNode == null || typeNode == null
                ? -1
                : parentNode.Children.IndexOf(typeNode.TreeNode);
            var wasSelected = typeNode != null && typeNode.TreeNode.TreeView.SelectedItem == typeNode;

            try
            {
                // Reinsert a visible node so its parent's sort order is recalculated.
                if (parentNode != null && typeNode != null && originalIndex >= 0)
                    parentNode.Children.RemoveAt(originalIndex);

                type.Name = newUtf8Name;
                foreach (var typeRef in typeRefs)
                    typeRef.Name = newUtf8Name;

                if (parentNode != null && typeNode != null && originalIndex >= 0)
                    parentNode!.AddChild(typeNode!.TreeNode);
            }
            catch
            {
                type.Name = new UTF8String(oldName);
                foreach (var typeRef in typeRefs)
                    typeRef.Name = new UTF8String(oldName);
                if (parentNode != null && typeNode != null && originalIndex >= 0)
                {
                    var currentIndex = parentNode.Children.IndexOf(typeNode.TreeNode);
                    if (currentIndex >= 0)
                        parentNode.Children.RemoveAt(currentIndex);
                    parentNode.Children.Insert(Math.Min(originalIndex, parentNode.Children.Count), typeNode.TreeNode);
                }
                throw;
            }

            // UI refresh failures must not report the metadata operation as failed after it has
            // already committed. Log the warning; the renamed metadata can still be saved.
            try
            {
                if (wasSelected && typeNode != null)
                    typeNode.TreeNode.TreeView.SelectItems(new[] { typeNode });
                typeNode?.TreeNode.RefreshUI();
            }
            catch (Exception ex)
            {
                settings.Log($"rename_symbol_by_token[type] UI refresh warning: {ex.Message}");
            }
            RefreshDecompiledViews(module, "rename_symbol_by_token");

            settings.Log(
                $"rename_symbol_by_token[{typeKind}]: {module.Assembly?.Name.String ?? module.Name.String} " +
                $"0x{token:X8} {oldFullName} → {type.FullName} ({typeRefs.Length} TypeRefs updated)");

            return CreateRenameTypeResult(
                module, type, token, typeKind, oldName, oldFullName, typeRefs.Length, changed: true);
        }

        sealed class EnumMemberRenameRequest
        {
            public string Name { get; }
            public decimal Value { get; }

            public EnumMemberRenameRequest(string name, decimal value)
            {
                Name = name;
                Value = value;
            }
        }

        sealed class EnumMemberRenameEdit
        {
            public FieldDef Field { get; }
            public string OldName { get; }
            public string NewName { get; }
            public decimal Value { get; }
            public List<(MemberRef memberRef, UTF8String oldName)> MemberRefs { get; }

            public EnumMemberRenameEdit(
                FieldDef field,
                string newName,
                decimal value,
                List<(MemberRef memberRef, UTF8String oldName)> memberRefs)
            {
                Field = field;
                OldName = field.Name.String;
                NewName = newName;
                Value = value;
                MemberRefs = memberRefs;
            }
        }

        CallToolResult RenameEnumMembersSymbol(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");

            var token = ReadOptionalUInt(arguments, "token")
                ?? throw new ArgumentException("token is required (enum TypeDef MDToken.Raw — decimal uint or '0x' hex string)");
            if ((token & 0xFF000000U) != 0x02000000U)
                throw new ArgumentException($"Token 0x{token:X8} is not a TypeDef token (expected table prefix 0x02).");

            var requests = ParseEnumMemberRenameRequests(arguments);
            var assemblyName = ReadOptionalString(arguments, "assembly_name");
            var (module, type) = ResolveTypeByToken(token, assemblyName);
            if (!type.IsEnum)
                throw new ArgumentException($"Type {type.FullName} (0x{token:X8}) is not an enum.");

            var literalFields = type.Fields
                .Where(f => f.IsStatic && f.IsLiteral && f.HasConstant && f.Constant?.Value != null)
                .ToList();
            if (literalFields.Count == 0)
                throw new ArgumentException($"Enum {type.FullName} has no literal fields.");
            if (literalFields.Count != requests.Count)
                throw new ArgumentException(
                    $"members must describe every literal in {type.FullName}: expected {literalFields.Count}, got {requests.Count}.");

            var fieldsByValue = literalFields
                .Select(f => (field: f, value: ReadEnumConstant(f)))
                .GroupBy(x => x.value)
                .ToDictionary(g => g.Key, g => g.Select(x => x.field).ToList());
            var aliasedValue = fieldsByValue.FirstOrDefault(p => p.Value.Count != 1);
            if (aliasedValue.Value != null)
                throw new ArgumentException(
                    $"Enum {type.FullName} has {aliasedValue.Value.Count} aliases for value " +
                    $"{FormatEnumValue(aliasedValue.Key)}; value-only member mapping would be ambiguous.");

            var requestedValues = new HashSet<decimal>(requests.Select(r => r.Value));
            var existingValues = new HashSet<decimal>(fieldsByValue.Keys);
            if (!requestedValues.SetEquals(existingValues))
                throw new ArgumentException(
                    $"members values must exactly match the enum's existing values. " +
                    $"Existing: {string.Join(", ", existingValues.OrderBy(v => v).Select(FormatEnumValue))}; " +
                    $"requested: {string.Join(", ", requestedValues.OrderBy(v => v).Select(FormatEnumValue))}.");

            var targetFields = requests.Select(r => fieldsByValue[r.Value][0]).ToHashSet();
            var requestedNames = new HashSet<string>(requests.Select(r => r.Name), StringComparer.Ordinal);
            var collision = type.Fields.FirstOrDefault(f =>
                !targetFields.Contains(f) && requestedNames.Contains(f.Name.String));
            if (collision != null)
                throw new ArgumentException(
                    $"Cannot rename enum members: '{collision.Name.String}' is already used by a non-target field.");

            var allMemberRefs = EnumerateFieldMemberRefs(module).ToArray();
            var edits = requests.Select(r =>
            {
                var field = fieldsByValue[r.Value][0];
                var refs = allMemberRefs
                    .Where(m => MemberRefTargetsField(m, field))
                    .Select(m => (memberRef: m, oldName: m.Name))
                    .ToList();
                return new EnumMemberRenameEdit(field, r.Name, r.Value, refs);
            }).ToList();

            try
            {
                foreach (var edit in edits)
                {
                    edit.Field.Name = new UTF8String(edit.NewName);
                    foreach (var (memberRef, _) in edit.MemberRefs)
                        memberRef.Name = edit.Field.Name;
                }
            }
            catch
            {
                foreach (var edit in edits)
                {
                    edit.Field.Name = new UTF8String(edit.OldName);
                    foreach (var (memberRef, oldName) in edit.MemberRefs)
                        memberRef.Name = oldName;
                }
                throw;
            }

            foreach (var edit in edits)
            {
                try { documentTreeView.FindNode(edit.Field)?.TreeNode.RefreshUI(); }
                catch (Exception ex) { settings.Log($"rename_symbol_by_token[enum_members] UI refresh warning: {ex.Message}"); }
            }
            RefreshDecompiledViews(module, "rename_symbol_by_token");

            var changedCount = edits.Count(e => !string.Equals(e.OldName, e.NewName, StringComparison.Ordinal));
            var updatedMemberReferences = edits.Sum(e => e.MemberRefs.Count);
            settings.Log(
                $"rename_symbol_by_token[enum_members]: {module.Assembly?.Name.String ?? module.Name.String} " +
                $"0x{token:X8} {type.FullName}, {changedCount}/{edits.Count} fields renamed " +
                $"({updatedMemberReferences} MemberRefs updated)");

            var result = new Dictionary<string, object?>
            {
                ["changed"] = changedCount != 0,
                ["changed_count"] = changedCount,
                ["assembly"] = module.Assembly?.Name.String ?? module.Name.String,
                ["module"] = module.Name.String,
                ["enum_token"] = token,
                ["enum_token_hex"] = $"0x{token:X8}",
                ["enum_full_name"] = type.FullName,
                ["updated_member_references"] = updatedMemberReferences,
                ["members"] = edits.Select(e => new
                {
                    field_token = e.Field.MDToken.Raw,
                    field_token_hex = $"0x{e.Field.MDToken.Raw:X8}",
                    value = e.Value,
                    old_name = e.OldName,
                    new_name = e.NewName,
                    changed = !string.Equals(e.OldName, e.NewName, StringComparison.Ordinal),
                    updated_member_references = e.MemberRefs.Count
                }).ToList(),
                ["note"] = changedCount != 0
                    ? "Renamed in dnSpy's in-memory metadata. Values were validated and left unchanged. Call save_assembly to persist the change. There is no revert for renames — rename back to the old names to undo. Only references inside this module are updated; other loaded assemblies that reference the old names are NOT rewritten and will no longer bind once this one is saved."
                    : "All requested names already match; no metadata names changed."
            };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = json }
                }
            };
        }

        static List<EnumMemberRenameRequest> ParseEnumMemberRenameRequests(Dictionary<string, object> arguments)
        {
            if (!arguments.TryGetValue("members", out var raw) || raw == null)
                throw new ArgumentException("members is required and must be a non-empty array");

            var requests = new List<EnumMemberRenameRequest>();
            if (raw is JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("members must be an array");
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("each members item must be an object with name and value");
                    if (!item.TryGetProperty("name", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("each members item requires a string name");
                    if (!item.TryGetProperty("value", out var valueElement))
                        throw new ArgumentException("each members item requires an integral value");
                    requests.Add(new EnumMemberRenameRequest(
                        ValidateEnumMemberName(nameElement.GetString()),
                        ParseIntegralEnumValue(valueElement)));
                }
            }
            else if (raw is IEnumerable<object> sequence)
            {
                foreach (var item in sequence)
                {
                    if (item is not Dictionary<string, object> dictionary)
                        throw new ArgumentException("each members item must be an object with name and value");
                    var name = ReadOptionalString(dictionary, "name");
                    if (!dictionary.TryGetValue("value", out var value) || value == null)
                        throw new ArgumentException("each members item requires an integral value");
                    requests.Add(new EnumMemberRenameRequest(
                        ValidateEnumMemberName(name),
                        ParseIntegralEnumValue(value)));
                }
            }
            else
                throw new ArgumentException("members must be an array");

            if (requests.Count == 0)
                throw new ArgumentException("members must contain at least one item");
            if (requests.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != requests.Count)
                throw new ArgumentException("members contains duplicate names");
            if (requests.Select(r => r.Value).Distinct().Count() != requests.Count)
                throw new ArgumentException("members contains duplicate values; aliases cannot be mapped by value alone");
            return requests;
        }

        static string ValidateEnumMemberName(string? name)
        {
            name = name?.Trim();
            if (name == null || name.Length == 0)
                throw new ArgumentException("enum member name cannot be empty");
            if (name.IndexOf('\0') >= 0)
                throw new ArgumentException("enum member name cannot contain a NUL character");
            if (name == "value__")
                throw new ArgumentException("'value__' is reserved for the enum's backing field");
            return name;
        }

        static decimal ParseIntegralEnumValue(object raw)
        {
            decimal value;
            if (raw is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
                    return decimal.Truncate(value) == value
                        ? value
                        : throw new ArgumentException("enum member value must be an integer");
                if (element.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return value;
                throw new ArgumentException("enum member value must be a decimal integer or numeric string");
            }

            if (raw is string text &&
                decimal.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
            try { value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture); }
            catch (Exception) { throw new ArgumentException("enum member value must be an integer"); }
            if (decimal.Truncate(value) != value)
                throw new ArgumentException("enum member value must be an integer");
            return value;
        }

        static decimal ReadEnumConstant(FieldDef field)
        {
            try { return Convert.ToDecimal(field.Constant!.Value, CultureInfo.InvariantCulture); }
            catch (Exception)
            {
                throw new ArgumentException(
                    $"Enum field {field.FullName} has a non-integral or unsupported constant value.");
            }
        }

        static string FormatEnumValue(decimal value) =>
            value.ToString("0", CultureInfo.InvariantCulture);

        static bool MemberRefTargetsField(MemberRef memberRef, FieldDef field)
        {
            if (!memberRef.IsFieldRef)
                return false;
            try
            {
                if (ReferenceEquals(memberRef.ResolveField(), field))
                    return true;
            }
            catch { /* fall back to signature comparison */ }

            if (!new SigComparer(RenameMemberComparerOptions).Equals(memberRef, field))
                return false;
            return MemberRefParentMatchesType(memberRef.Class, field.DeclaringType);
        }

        static bool MemberRefTargetsMethod(MemberRef memberRef, MethodDef method)
        {
            if (!memberRef.IsMethodRef)
                return false;
            try
            {
                if (ReferenceEquals(memberRef.ResolveMethod(), method))
                    return true;
            }
            catch { /* fall back to signature comparison */ }

            if (!new SigComparer(RenameMemberComparerOptions).Equals(memberRef, method))
                return false;
            return MemberRefParentMatchesType(memberRef.Class, method.DeclaringType);
        }

        static IEnumerable<MemberRef> EnumerateMethodMemberRefs(ModuleDef module)
        {
            // ModuleDef.GetMemberRefs() covers the MemberRef table, but dnlib can materialize a
            // separate MemberRef object under MethodSpec.Method for a call on a closed generic
            // declaring type. Update both representations, plus the exact operands held by method
            // bodies, so the live decompiler and the module writer observe the new name.
            foreach (var memberRef in module.GetMemberRefs())
                yield return memberRef;

            foreach (var owner in module.GetTypes().SelectMany(t => t.Methods))
            {
                if (!owner.HasBody)
                    continue;
                foreach (var instruction in owner.Body.Instructions)
                {
                    if (instruction.Operand is MemberRef memberRef)
                        yield return memberRef;
                    else if (instruction.Operand is MethodSpec methodSpec &&
                        methodSpec.Method is MemberRef methodSpecMemberRef)
                        yield return methodSpecMemberRef;
                }
            }
        }

        static bool MemberRefParentMatchesType(IMemberRefParent parent, TypeDef type)
        {
            var comparer = new TypeEqualityComparer(RenameTypeComparerOptions);
            if (parent is TypeDef typeDef)
                return comparer.Equals(typeDef, type);
            if (parent is TypeRef typeRef)
                return comparer.Equals(typeRef, type);
            if (parent is TypeSpec typeSpec)
            {
                var typeSig = typeSpec.TypeSig.RemovePinnedAndModifiers();
                var typeDefOrRefSig = typeSig as TypeDefOrRefSig;
                if (typeDefOrRefSig == null && typeSig is GenericInstSig genericInstSig)
                    typeDefOrRefSig = genericInstSig.GenericType;
                return typeDefOrRefSig != null && comparer.Equals(typeDefOrRefSig.TypeDefOrRef, type);
            }
            if (parent is MethodDef methodDef)
                return comparer.Equals(methodDef.DeclaringType, type);
            return parent is ModuleRef moduleRef &&
                type.IsGlobalModuleType &&
                StringComparer.OrdinalIgnoreCase.Equals(moduleRef.Name, type.Module.Name);
        }

        void RefreshDecompiledViews(ModuleDef module, string operation)
        {
            var moduleNode = documentTreeView.FindNode(module);
            if (moduleNode?.Document == null)
            {
                settings.Log($"{operation}: module document node not found; open decompiler tabs were not refreshed");
                return;
            }

            // RefreshUI() only redraws the assembly-tree label. This public API is the
            // invalidation path dnSpy.AsmEditor ultimately uses to rebuild decompiled tabs after
            // its undo commands report modified document-tree objects.
            documentTabService.RefreshModifiedDocument(moduleNode.Document);
        }

        (ModuleDef module, TypeDef type) ResolveTypeByToken(uint token, string? assemblyName)
        {
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
                    .Select(n => n.Document?.ModuleDef)
                    .Where(m => m != null)!
                    .Cast<ModuleDef>();
            }

            var hits = modules
                .SelectMany(m => m.GetTypes()
                    .Where(t => t.MDToken.Raw == token)
                    .Select(t => (module: m, type: t)))
                .ToList();

            if (hits.Count == 0)
                throw new ArgumentException(
                    $"No class or enum with TypeDef MDToken 0x{token:X8} in {(assemblyName ?? "any loaded assembly")}. " +
                    "Pass assembly_name if the token came from a specific module.");
            if (hits.Count > 1)
                throw new ArgumentException(
                    $"TypeDef MDToken 0x{token:X8} is ambiguous across {hits.Count} modules " +
                    $"({string.Join(", ", hits.Select(h => h.module.Assembly?.Name.String ?? h.module.Name.String))}). " +
                    "Pass assembly_name to disambiguate.");

            return hits[0];
        }

        (ModuleDef module, MethodDef method) ResolveMethodByToken(uint token, string? assemblyName)
        {
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
                    .Select(n => n.Document?.ModuleDef)
                    .Where(m => m != null)!
                    .Cast<ModuleDef>();
            }

            var hits = modules
                .SelectMany(m => m.GetTypes()
                    .SelectMany(t => t.Methods)
                    .Where(method => method.MDToken.Raw == token)
                    .Select(method => (module: m, method)))
                .ToList();

            if (hits.Count == 0)
                throw new ArgumentException(
                    $"No method with MethodDef MDToken 0x{token:X8} in {(assemblyName ?? "any loaded assembly")}. " +
                    "Pass assembly_name if the token came from a specific module.");
            if (hits.Count > 1)
                throw new ArgumentException(
                    $"MethodDef MDToken 0x{token:X8} is ambiguous across {hits.Count} modules " +
                    $"({string.Join(", ", hits.Select(h => h.module.Assembly?.Name.String ?? h.module.Name.String))}). " +
                    "Pass assembly_name to disambiguate.");

            return hits[0];
        }

        static CallToolResult CreateRenameMethodResult(
            ModuleDef module,
            MethodDef method,
            uint token,
            string oldName,
            string oldFullName,
            int updatedMemberReferences,
            bool changed)
        {
            var result = new Dictionary<string, object?>
            {
                ["changed"] = changed,
                ["assembly"] = module.Assembly?.Name.String ?? module.Name.String,
                ["module"] = module.Name.String,
                ["token"] = token,
                ["token_hex"] = $"0x{token:X8}",
                ["declaring_type"] = method.DeclaringType.FullName,
                ["old_name"] = oldName,
                ["new_name"] = method.Name.String,
                ["old_full_name"] = oldFullName,
                ["new_full_name"] = method.FullName,
                ["updated_member_references"] = updatedMemberReferences,
                ["note"] = changed
                    ? "Renamed in dnSpy's in-memory metadata. Call save_assembly to persist the change to disk. There is no revert for renames — rename back to the old name to undo. Only references inside this module are updated; other loaded assemblies that reference the old name are NOT rewritten and will no longer bind once this one is saved."
                    : "The requested name already matches the current metadata name; no change was made."
            };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = json }
                }
            };
        }

        static CallToolResult CreateRenameTypeResult(
            ModuleDef module,
            TypeDef type,
            uint token,
            string typeKind,
            string oldName,
            string oldFullName,
            int updatedTypeReferences,
            bool changed)
        {
            var result = new Dictionary<string, object?>
            {
                ["changed"] = changed,
                ["assembly"] = module.Assembly?.Name.String ?? module.Name.String,
                ["module"] = module.Name.String,
                ["token"] = token,
                ["token_hex"] = $"0x{token:X8}",
                ["type_kind"] = typeKind,
                ["old_name"] = oldName,
                ["new_name"] = type.Name.String,
                ["old_full_name"] = oldFullName,
                ["new_full_name"] = type.FullName,
                ["updated_type_references"] = updatedTypeReferences,
                ["note"] = changed
                    ? "Renamed in dnSpy's in-memory metadata. Call save_assembly to persist the change to disk. There is no revert for renames — rename back to the old name to undo. Only references inside this module are updated; other loaded assemblies that reference the old name are NOT rewritten and will no longer bind once this one is saved."
                    : "The requested name already matches the current metadata name; no change was made."
            };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = json }
                }
            };
        }
    }
}
