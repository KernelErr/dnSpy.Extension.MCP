using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP
{
    sealed partial class McpTools
    {
        static readonly HashSet<string> RenameSymbolKinds = new HashSet<string>(
            new[] {
                "type", "class", "enum", "interface", "struct", "delegate",
                "method", "field", "enum_member", "enum_members",
                "property", "event", "parameter", "generic_parameter"
            },
            StringComparer.Ordinal);

        CallToolResult RenameSymbolByToken(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");

            var targetKind = ReadOptionalString(arguments, "target_kind")?.Trim().ToLowerInvariant()
                ?? throw new ArgumentException(
                    "target_kind is required (type/class/enum/interface/struct/delegate/method/field/" +
                    "enum_member/enum_members/property/event/parameter/generic_parameter)");
            if (targetKind == "param")
                targetKind = "parameter";
            else if (targetKind == "generic_param")
                targetKind = "generic_parameter";
            if (!RenameSymbolKinds.Contains(targetKind))
                throw new ArgumentException(
                    $"Unsupported target_kind '{targetKind}'. Supported values: " +
                    string.Join(", ", RenameSymbolKinds.OrderBy(k => k)));

            if (targetKind == "method")
                return RenameMethodSymbol(arguments);
            if (targetKind == "enum_members")
                return RenameEnumMembersSymbol(arguments);

            var token = ReadOptionalUInt(arguments, "token")
                ?? throw new ArgumentException("token is required (decimal uint or '0x' hex metadata token)");
            var newName = ReadRenameSymbolName(arguments);
            var assemblyName = ReadOptionalString(arguments, "assembly_name");

            return targetKind switch
            {
                "type" or "class" or "enum" or "interface" or "struct" or "delegate" =>
                    RenameAnyTypeSymbol(token, targetKind, newName, assemblyName),
                "field" => RenameFieldSymbol(token, newName, assemblyName, requireEnumMember: false),
                "enum_member" => RenameFieldSymbol(token, newName, assemblyName, requireEnumMember: true),
                "property" => RenamePropertySymbol(token, newName, assemblyName),
                "event" => RenameEventSymbol(token, newName, assemblyName),
                "parameter" => RenameParameterSymbol(token, newName, assemblyName),
                "generic_parameter" => RenameGenericParameterSymbol(token, newName, assemblyName),
                _ => throw new ArgumentException($"Unsupported target_kind '{targetKind}'.")
            };
        }

        static string ReadRenameSymbolName(Dictionary<string, object> arguments)
        {
            var newName = ReadOptionalString(arguments, "new_name")
                ?? throw new ArgumentException("new_name is required and cannot be empty");
            newName = newName.Trim();
            if (newName.IndexOf('\0') >= 0)
                throw new ArgumentException("new_name cannot contain a NUL character");
            return newName;
        }

        CallToolResult RenameAnyTypeSymbol(
            uint token,
            string requestedKind,
            string newName,
            string? assemblyName)
        {
            RequireTokenTable(token, 0x02000000U, "TypeDef");
            var (module, type) = ResolveTypeByToken(token, assemblyName);
            if (type.IsGlobalModuleType)
                throw new ArgumentException("The special <Module> type cannot be renamed.");

            var actualKind = GetTypeSymbolKind(type);
            if (requestedKind != "type" && requestedKind != actualKind)
                throw new ArgumentException(
                    $"target_kind '{requestedKind}' does not match {type.FullName}, which is a {actualKind}.");

            var oldName = type.Name.String;
            var oldFullName = type.FullName;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return CreateSymbolRenameResult(
                    module, token, actualKind, type.FullName, oldName, newName, 0, false);

            var duplicate = type.DeclaringType != null
                ? type.DeclaringType.NestedTypes.Any(t => t != type && t.Name.String == newName)
                : module.Types.Any(t =>
                    t != type &&
                    t.Namespace.String == type.Namespace.String &&
                    t.Name.String == newName);
            if (duplicate)
                throw new ArgumentException(
                    $"A sibling type named '{newName}' already exists in " +
                    $"{(type.DeclaringType?.FullName ?? type.Namespace.String)}.");

            var comparer = new TypeEqualityComparer(RenameTypeComparerOptions);
            var typeRefs = module.GetTypeRefs()
                .Where(t => comparer.Equals(t, type))
                .Select(t => (typeRef: t, oldName: t.Name))
                .ToList();
            var newUtf8Name = new UTF8String(newName);

            try
            {
                type.Name = newUtf8Name;
                foreach (var (typeRef, _) in typeRefs)
                    typeRef.Name = newUtf8Name;
            }
            catch
            {
                type.Name = new UTF8String(oldName);
                foreach (var (typeRef, typeRefOldName) in typeRefs)
                    typeRef.Name = typeRefOldName;
                throw;
            }

            RefreshSymbolNode(type, module, "rename_symbol_by_token");
            settings.Log(
                $"rename_symbol_by_token[{actualKind}]: {module.Assembly?.Name.String ?? module.Name.String} " +
                $"0x{token:X8} {oldFullName} → {type.FullName} ({typeRefs.Count} TypeRefs updated)");
            return CreateSymbolRenameResult(
                module, token, actualKind, type.FullName, oldName, newName, typeRefs.Count, true,
                oldFullName, type.FullName);
        }

        CallToolResult RenameFieldSymbol(
            uint token,
            string newName,
            string? assemblyName,
            bool requireEnumMember)
        {
            RequireTokenTable(token, 0x04000000U, "FieldDef");
            var (module, field) = ResolveFieldByToken(token, assemblyName);
            var isEnumMember = field.DeclaringType.IsEnum && field.IsStatic && field.IsLiteral;
            if (requireEnumMember && !isEnumMember)
                throw new ArgumentException(
                    $"Field {field.FullName} (0x{token:X8}) is not a literal enum member.");
            if (field.DeclaringType.IsEnum && field.Name.String == "value__")
                throw new ArgumentException("The enum backing field 'value__' cannot be renamed.");
            if (newName == "value__" && field.DeclaringType.IsEnum)
                throw new ArgumentException("'value__' is reserved for the enum's backing field.");

            var oldName = field.Name.String;
            var oldFullName = field.FullName;
            var targetKind = isEnumMember ? "enum_member" : "field";
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return CreateSymbolRenameResult(
                    module, token, targetKind, field.DeclaringType.FullName, oldName, newName, 0, false);

            var duplicate = field.DeclaringType.Fields.FirstOrDefault(f =>
                f != field && string.Equals(f.Name.String, newName, StringComparison.Ordinal));
            if (duplicate != null)
                throw new ArgumentException(
                    $"Field {field.DeclaringType.FullName} already contains '{newName}' " +
                    $"(0x{duplicate.MDToken.Raw:X8}).");

            var memberRefs = EnumerateFieldMemberRefs(module)
                .Where(m => MemberRefTargetsField(m, field))
                .Select(m => (memberRef: m, oldName: m.Name))
                .ToList();
            var newUtf8Name = new UTF8String(newName);
            try
            {
                field.Name = newUtf8Name;
                foreach (var (memberRef, _) in memberRefs)
                    memberRef.Name = newUtf8Name;
            }
            catch
            {
                field.Name = new UTF8String(oldName);
                foreach (var (memberRef, memberRefOldName) in memberRefs)
                    memberRef.Name = memberRefOldName;
                throw;
            }

            RefreshSymbolNode(field, module, "rename_symbol_by_token");
            var updatedReferences = CountMemberRefRows(memberRefs.Select(r => r.memberRef));
            settings.Log(
                $"rename_symbol_by_token[{targetKind}]: {module.Assembly?.Name.String ?? module.Name.String} " +
                $"0x{token:X8} {oldFullName} → {field.FullName} ({updatedReferences} MemberRefs updated)");
            return CreateSymbolRenameResult(
                module, token, targetKind, field.DeclaringType.FullName,
                oldName, newName, updatedReferences, true, oldFullName, field.FullName);
        }

        CallToolResult RenamePropertySymbol(
            uint token,
            string newName,
            string? assemblyName)
        {
            RequireTokenTable(token, 0x17000000U, "Property");
            var (module, property) = ResolvePropertyByToken(token, assemblyName);
            var oldName = property.Name.String;
            var oldFullName = property.FullName;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return CreateSymbolRenameResult(
                    module, token, "property", property.DeclaringType.FullName, oldName, newName, 0, false);

            var comparer = new SigComparer(RenameMemberComparerOptions);
            var duplicate = property.DeclaringType.Properties.FirstOrDefault(p =>
                p != property &&
                string.Equals(p.Name.String, newName, StringComparison.Ordinal) &&
                comparer.Equals(p.PropertySig, property.PropertySig));
            if (duplicate != null)
                throw new ArgumentException(
                    $"Property {property.DeclaringType.FullName} already contains '{newName}' with the same signature " +
                    $"(0x{duplicate.MDToken.Raw:X8}).");

            property.Name = new UTF8String(newName);
            RefreshSymbolNode(property, module, "rename_symbol_by_token");
            settings.Log(
                $"rename_symbol_by_token[property]: {module.Assembly?.Name.String ?? module.Name.String} " +
                $"0x{token:X8} {oldFullName} → {property.FullName}");
            return CreateSymbolRenameResult(
                module, token, "property", property.DeclaringType.FullName,
                oldName, newName, 0, true, oldFullName, property.FullName,
                "Accessor MethodDef names are unchanged; rename them separately by token if desired.");
        }

        CallToolResult RenameEventSymbol(
            uint token,
            string newName,
            string? assemblyName)
        {
            RequireTokenTable(token, 0x14000000U, "Event");
            var (module, eventDef) = ResolveEventByToken(token, assemblyName);
            var oldName = eventDef.Name.String;
            var oldFullName = eventDef.FullName;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return CreateSymbolRenameResult(
                    module, token, "event", eventDef.DeclaringType.FullName, oldName, newName, 0, false);

            var duplicate = eventDef.DeclaringType.Events.FirstOrDefault(e =>
                e != eventDef && string.Equals(e.Name.String, newName, StringComparison.Ordinal));
            if (duplicate != null)
                throw new ArgumentException(
                    $"Event {eventDef.DeclaringType.FullName} already contains '{newName}' " +
                    $"(0x{duplicate.MDToken.Raw:X8}).");

            eventDef.Name = new UTF8String(newName);
            RefreshSymbolNode(eventDef, module, "rename_symbol_by_token");
            settings.Log(
                $"rename_symbol_by_token[event]: {module.Assembly?.Name.String ?? module.Name.String} " +
                $"0x{token:X8} {oldFullName} → {eventDef.FullName}");
            return CreateSymbolRenameResult(
                module, token, "event", eventDef.DeclaringType.FullName,
                oldName, newName, 0, true, oldFullName, eventDef.FullName,
                "Event accessor MethodDef names are unchanged; rename them separately by token if desired.");
        }

        CallToolResult RenameParameterSymbol(
            uint token,
            string newName,
            string? assemblyName)
        {
            RequireTokenTable(token, 0x08000000U, "Param");
            var (module, parameter) = ResolveParameterByToken(token, assemblyName);
            var method = parameter.DeclaringMethod
                ?? throw new ArgumentException($"Parameter 0x{token:X8} has no declaring method.");
            if (parameter.Sequence == 0)
                throw new ArgumentException("The return-parameter metadata row (sequence 0) cannot be renamed.");

            var oldName = parameter.Name.String;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return CreateSymbolRenameResult(
                    module, token, "parameter", method.FullName, oldName, newName, 0, false);
            var duplicate = method.ParamDefs.FirstOrDefault(p =>
                p != parameter &&
                p.Sequence != 0 &&
                string.Equals(p.Name.String, newName, StringComparison.Ordinal));
            if (duplicate != null)
                throw new ArgumentException(
                    $"Method {method.FullName} already contains a parameter named '{newName}' " +
                    $"(0x{duplicate.MDToken.Raw:X8}).");

            parameter.Name = new UTF8String(newName);
            RefreshSymbolOwnerNode(method, module, "rename_symbol_by_token");
            settings.Log(
                $"rename_symbol_by_token[parameter]: {module.Assembly?.Name.String ?? module.Name.String} " +
                $"0x{token:X8} {method.FullName} parameter '{oldName}' → '{newName}'");
            return CreateSymbolRenameResult(
                module, token, "parameter", method.FullName, oldName, newName, 0, true);
        }

        CallToolResult RenameGenericParameterSymbol(
            uint token,
            string newName,
            string? assemblyName)
        {
            RequireTokenTable(token, 0x2A000000U, "GenericParam");
            var (module, genericParameter) = ResolveGenericParameterByToken(token, assemblyName);
            var owner = genericParameter.Owner
                ?? throw new ArgumentException($"Generic parameter 0x{token:X8} has no owner.");
            var ownerName = owner is TypeDef typeOwner
                ? typeOwner.FullName
                : owner is MethodDef methodOwner
                    ? methodOwner.FullName
                    : owner.ToString() ?? "<unknown>";
            var siblings = owner is TypeDef declaringType
                ? declaringType.GenericParameters
                : ((MethodDef)owner).GenericParameters;
            var oldName = genericParameter.Name.String;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return CreateSymbolRenameResult(
                    module, token, "generic_parameter", ownerName, oldName, newName, 0, false);
            var duplicate = siblings.FirstOrDefault(p =>
                p != genericParameter && string.Equals(p.Name.String, newName, StringComparison.Ordinal));
            if (duplicate != null)
                throw new ArgumentException(
                    $"{ownerName} already contains a generic parameter named '{newName}' " +
                    $"(0x{duplicate.MDToken.Raw:X8}).");

            genericParameter.Name = new UTF8String(newName);
            if (owner is TypeDef ownerType)
                RefreshSymbolOwnerNode(ownerType, module, "rename_symbol_by_token");
            else
                RefreshSymbolOwnerNode((MethodDef)owner, module, "rename_symbol_by_token");
            settings.Log(
                $"rename_symbol_by_token[generic_parameter]: {module.Assembly?.Name.String ?? module.Name.String} " +
                $"0x{token:X8} {ownerName} '{oldName}' → '{newName}'");
            return CreateSymbolRenameResult(
                module, token, "generic_parameter", ownerName, oldName, newName, 0, true);
        }

        static string GetTypeSymbolKind(TypeDef type)
        {
            if (type.IsEnum)
                return "enum";
            if (type.IsInterface)
                return "interface";
            if (type.IsValueType)
                return "struct";
            var baseType = type.BaseType?.FullName;
            if (baseType == "System.MulticastDelegate" || baseType == "System.Delegate")
                return "delegate";
            return "class";
        }

        static void RequireTokenTable(uint token, uint expectedPrefix, string tableName)
        {
            if ((token & 0xFF000000U) != expectedPrefix)
                throw new ArgumentException(
                    $"Token 0x{token:X8} is not a {tableName} token " +
                    $"(expected table prefix 0x{expectedPrefix >> 24:X2}).");
        }

        IEnumerable<ModuleDef> ResolveRenameModules(string? assemblyName)
        {
            if (assemblyName != null)
            {
                var assembly = FindAssemblyByName(assemblyName);
                if (assembly == null)
                    throw new ArgumentException($"Assembly not found: {assemblyName}");
                return assembly.Modules;
            }
            return documentTreeView.GetAllModuleNodes()
                .Select(n => n.Document?.ModuleDef)
                .Where(m => m != null)!
                .Cast<ModuleDef>();
        }

        (ModuleDef module, FieldDef field) ResolveFieldByToken(uint token, string? assemblyName) =>
            ResolveRenameDefinition(
                token, assemblyName, "field",
                module => module.GetTypes().SelectMany(type => type.Fields));

        (ModuleDef module, PropertyDef property) ResolvePropertyByToken(uint token, string? assemblyName) =>
            ResolveRenameDefinition(
                token, assemblyName, "property",
                module => module.GetTypes().SelectMany(type => type.Properties));

        (ModuleDef module, EventDef eventDef) ResolveEventByToken(uint token, string? assemblyName) =>
            ResolveRenameDefinition(
                token, assemblyName, "event",
                module => module.GetTypes().SelectMany(type => type.Events));

        (ModuleDef module, ParamDef parameter) ResolveParameterByToken(uint token, string? assemblyName) =>
            ResolveRenameDefinition(
                token, assemblyName, "parameter",
                module => module.GetTypes().SelectMany(type => type.Methods).SelectMany(method => method.ParamDefs));

        (ModuleDef module, GenericParam genericParameter) ResolveGenericParameterByToken(
            uint token,
            string? assemblyName) =>
            ResolveRenameDefinition(
                token, assemblyName, "generic parameter",
                module => module.GetTypes().SelectMany(type =>
                    type.GenericParameters.Concat(type.Methods.SelectMany(method => method.GenericParameters))));

        (ModuleDef module, T definition) ResolveRenameDefinition<T>(
            uint token,
            string? assemblyName,
            string description,
            Func<ModuleDef, IEnumerable<T>> selector)
            where T : class, IMDTokenProvider
        {
            var hits = ResolveRenameModules(assemblyName)
                .SelectMany(module => selector(module)
                    .Where(definition => definition.MDToken.Raw == token)
                    .Select(definition => (module, definition)))
                .ToList();
            if (hits.Count == 0)
                throw new ArgumentException(
                    $"No {description} with metadata token 0x{token:X8} in " +
                    $"{(assemblyName ?? "any loaded assembly")}. Pass assembly_name if the token came from a specific module.");
            if (hits.Count > 1)
                throw new ArgumentException(
                    $"Metadata token 0x{token:X8} is ambiguous across {hits.Count} modules " +
                    $"({string.Join(", ", hits.Select(h => h.module.Assembly?.Name.String ?? h.module.Name.String))}). " +
                    "Pass assembly_name to disambiguate.");
            return hits[0];
        }

        static IEnumerable<MemberRef> EnumerateFieldMemberRefs(ModuleDef module)
        {
            foreach (var memberRef in module.GetMemberRefs().Where(m => m.IsFieldRef))
                yield return memberRef;
            foreach (var owner in module.GetTypes().SelectMany(type => type.Methods))
            {
                if (!owner.HasBody)
                    continue;
                foreach (var instruction in owner.Body.Instructions)
                {
                    if (instruction.Operand is MemberRef memberRef && memberRef.IsFieldRef)
                        yield return memberRef;
                }
            }
        }

        static int CountMemberRefRows(IEnumerable<MemberRef> memberRefs)
        {
            var refs = memberRefs.ToList();
            var count = refs.Select(r => r.MDToken.Raw).Where(token => token != 0).Distinct().Count();
            return count == 0 ? refs.Count : count;
        }

        void RefreshSymbolNode(TypeDef type, ModuleDef module, string operation)
        {
            try { documentTreeView.FindNode(type)?.TreeNode.RefreshUI(); }
            catch (Exception ex) { settings.Log($"{operation} UI refresh warning: {ex.Message}"); }
            RefreshDecompiledViews(module, operation);
        }

        void RefreshSymbolNode(FieldDef field, ModuleDef module, string operation)
        {
            try { documentTreeView.FindNode(field)?.TreeNode.RefreshUI(); }
            catch (Exception ex) { settings.Log($"{operation} UI refresh warning: {ex.Message}"); }
            RefreshDecompiledViews(module, operation);
        }

        void RefreshSymbolNode(PropertyDef property, ModuleDef module, string operation)
        {
            try { documentTreeView.FindNode(property)?.TreeNode.RefreshUI(); }
            catch (Exception ex) { settings.Log($"{operation} UI refresh warning: {ex.Message}"); }
            RefreshDecompiledViews(module, operation);
        }

        void RefreshSymbolNode(EventDef eventDef, ModuleDef module, string operation)
        {
            try { documentTreeView.FindNode(eventDef)?.TreeNode.RefreshUI(); }
            catch (Exception ex) { settings.Log($"{operation} UI refresh warning: {ex.Message}"); }
            RefreshDecompiledViews(module, operation);
        }

        void RefreshSymbolOwnerNode(TypeDef type, ModuleDef module, string operation) =>
            RefreshSymbolNode(type, module, operation);

        void RefreshSymbolOwnerNode(MethodDef method, ModuleDef module, string operation)
        {
            try { documentTreeView.FindNode(method)?.TreeNode.RefreshUI(); }
            catch (Exception ex) { settings.Log($"{operation} UI refresh warning: {ex.Message}"); }
            RefreshDecompiledViews(module, operation);
        }

        static CallToolResult CreateSymbolRenameResult(
            ModuleDef module,
            uint token,
            string targetKind,
            string owner,
            string oldName,
            string newName,
            int updatedMemberReferences,
            bool changed,
            string? oldFullName = null,
            string? newFullName = null,
            string? extraNote = null)
        {
            var note = changed
                ? "Renamed in dnSpy's in-memory metadata. Call save_assembly to persist the change to disk. There is no revert for renames — rename back to the old name to undo. Only references inside this module are updated; other loaded assemblies that reference the old name are NOT rewritten and will no longer bind once this one is saved."
                : "The requested name already matches the current metadata name; no change was made.";
            if (!string.IsNullOrEmpty(extraNote))
                note += " " + extraNote;

            var result = new Dictionary<string, object?>
            {
                ["changed"] = changed,
                ["assembly"] = module.Assembly?.Name.String ?? module.Name.String,
                ["module"] = module.Name.String,
                ["target_kind"] = targetKind,
                ["token"] = token,
                ["token_hex"] = $"0x{token:X8}",
                ["owner"] = owner,
                ["old_name"] = oldName,
                ["new_name"] = newName,
                ["updated_member_references"] = updatedMemberReferences,
                ["note"] = note
            };
            if (oldFullName != null)
                result["old_full_name"] = oldFullName;
            if (newFullName != null)
                result["new_full_name"] = newFullName;
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent {
                        Text = JsonSerializer.Serialize(
                            result,
                            new JsonSerializerOptions { WriteIndented = true })
                    }
                }
            };
        }
    }
}
