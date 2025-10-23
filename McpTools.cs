using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;

namespace dnSpy.Extension.MCP
{
    /// <summary>
    /// Implements MCP tools for analyzing .NET assemblies and generating code.
    /// Provides tools for listing assemblies, inspecting types, decompiling methods, and generating BepInEx plugins.
    /// </summary>
    [Export(typeof(McpTools))]
    sealed class McpTools
    {
        readonly IDocumentTreeView documentTreeView;
        readonly IDecompilerService decompilerService;

        /// <summary>
        /// Initializes the MCP tools with dnSpy services.
        /// </summary>
        [ImportingConstructor]
        public McpTools(IDocumentTreeView documentTreeView, IDecompilerService decompilerService)
        {
            this.documentTreeView = documentTreeView;
            this.decompilerService = decompilerService;
        }

        /// <summary>
        /// Gets the list of available MCP tools with their schemas.
        /// </summary>
        public List<ToolInfo> GetAvailableTools()
        {
            return new List<ToolInfo> {
                new ToolInfo {
                    Name = "list_assemblies",
                    Description = "List all loaded assemblies in dnSpy",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "get_assembly_info",
                    Description = "Get detailed information about a specific assembly",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response)"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "list_types",
                    Description = "List all types in an assembly or namespace",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["namespace"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional namespace filter"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response)"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "get_type_info",
                    Description = "Get detailed information about a specific type including its members",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type including namespace"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination of methods (opaque token from previous response)"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "decompile_method",
                    Description = "Decompile a specific method to C# code",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the method"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "search_types",
                    Description = "Search for types by name across all loaded assemblies",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["query"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Search query (supports wildcards)"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response)"
                            }
                        },
                        ["required"] = new List<string> { "query" }
                    }
                },
                new ToolInfo {
                    Name = "generate_bepinex_plugin",
                    Description = "Generate a BepInEx plugin template with hooks for specified methods",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["plugin_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the plugin"
                            },
                            ["plugin_guid"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "GUID for the plugin"
                            },
                            ["target_assembly"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Target assembly name"
                            },
                            ["hooks"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["description"] = "Array of methods to hook",
                                ["items"] = new Dictionary<string, object> {
                                    ["type"] = "object",
                                    ["properties"] = new Dictionary<string, object> {
                                        ["type_name"] = new Dictionary<string, object> { ["type"] = "string" },
                                        ["method_name"] = new Dictionary<string, object> { ["type"] = "string" }
                                    }
                                }
                            }
                        },
                        ["required"] = new List<string> { "plugin_name", "plugin_guid", "target_assembly" }
                    }
                },
                new ToolInfo {
                    Name = "get_type_fields",
                    Description = "Get fields from a type matching a name pattern (supports wildcards like *Bonus*)",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["pattern"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Field name pattern (supports * wildcard)"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response)"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "pattern" }
                    }
                },
                new ToolInfo {
                    Name = "get_type_property",
                    Description = "Get detailed information about a specific property from a type",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["property_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the property"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "property_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_path_to_type",
                    Description = "Find property/field chains connecting two types through their members. (e.g., PlayerState -> RpBonus)",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["from_type"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Starting type full name"
                            },
                            ["to_type"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Target type full name or partial name"
                            },
                            ["max_depth"] = new Dictionary<string, object> {
                                ["type"] = "number",
                                ["description"] = "Maximum search depth (default: 5)"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "from_type", "to_type" }
                    }
                }
            };
        }

        /// <summary>
        /// Executes a specific MCP tool by name with the given arguments.
        /// </summary>
        /// <param name="toolName">The name of the tool to execute.</param>
        /// <param name="arguments">Tool-specific arguments.</param>
        /// <returns>The tool execution result.</returns>
        public CallToolResult ExecuteTool(string toolName, Dictionary<string, object>? arguments)
        {
            try
            {
                return toolName switch
                {
                    "list_assemblies" => ListAssemblies(),
                    "get_assembly_info" => GetAssemblyInfo(arguments),
                    "list_types" => ListTypes(arguments),
                    "get_type_info" => GetTypeInfo(arguments),
                    "decompile_method" => DecompileMethod(arguments),
                    "search_types" => SearchTypes(arguments),
                    "generate_bepinex_plugin" => GenerateBepInExPlugin(arguments),
                    "get_type_fields" => GetTypeFields(arguments),
                    "get_type_property" => GetTypeProperty(arguments),
                    "find_path_to_type" => FindPathToType(arguments),
                    _ => new CallToolResult
                    {
                        Content = new List<ToolContent> {
                            new ToolContent { Text = $"Unknown tool: {toolName}" }
                        },
                        IsError = true
                    }
                };
            }
            catch (Exception ex)
            {
                return new CallToolResult
                {
                    Content = new List<ToolContent> {
                        new ToolContent { Text = $"Error executing tool {toolName}: {ex.Message}" }
                    },
                    IsError = true
                };
            }
        }

        CallToolResult ListAssemblies()
        {
            var assemblies = documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .Where(a => a != null)
                .Distinct()
                .Select(a => new
                {
                    Name = a!.Name.String,
                    Version = a.Version?.ToString() ?? "N/A",
                    FullName = a.FullName,
                    Culture = a.Culture ?? "neutral",
                    PublicKeyToken = a.PublicKeyToken?.ToString() ?? "null"
                })
                .ToList();

            var result = JsonSerializer.Serialize(assemblies, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult GetAssemblyInfo(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var modules = assembly.Modules.Select(m => new
            {
                Name = m.Name.String,
                Kind = m.Kind.ToString(),
                Architecture = m.Machine.ToString(),
                RuntimeVersion = m.RuntimeVersion
            }).ToList();

            var allNamespaces = assembly.Modules
                .SelectMany(m => m.Types)
                .Select(t => t.Namespace.String)
                .Distinct()
                .OrderBy(ns => ns)
                .ToList();

            var namespacesToReturn = allNamespaces.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allNamespaces.Count;

            var info = new Dictionary<string, object>
            {
                ["Name"] = assembly.Name.String,
                ["Version"] = assembly.Version?.ToString() ?? "N/A",
                ["FullName"] = assembly.FullName,
                ["Culture"] = assembly.Culture ?? "neutral",
                ["PublicKeyToken"] = assembly.PublicKeyToken?.ToString() ?? "null",
                ["Modules"] = modules,
                ["Namespaces"] = namespacesToReturn,
                ["NamespacesTotalCount"] = allNamespaces.Count,
                ["NamespacesReturnedCount"] = namespacesToReturn.Count,
                ["TypeCount"] = assembly.Modules.Sum(m => m.Types.Count)
            };

            if (hasMore)
            {
                info["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult ListTypes(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            string? namespaceFilter = null;
            if (arguments.TryGetValue("namespace", out var nsObj))
                namespaceFilter = nsObj.ToString();

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var types = assembly.Modules
                .SelectMany(m => m.Types)
                .Where(t => string.IsNullOrEmpty(namespaceFilter) || t.Namespace == namespaceFilter)
                .Select(t => new
                {
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    IsPublic = t.IsPublic,
                    IsClass = t.IsClass,
                    IsInterface = t.IsInterface,
                    IsEnum = t.IsEnum,
                    IsValueType = t.IsValueType,
                    IsAbstract = t.IsAbstract,
                    IsSealed = t.IsSealed,
                    BaseType = t.BaseType?.FullName ?? "None"
                })
                .ToList();

            return CreatePaginatedResponse(types, offset, pageSize);
        }

        CallToolResult GetTypeInfo(Dictionary<string, object>? arguments)
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
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var allMethods = type.Methods.Select(m => new
            {
                Name = m.Name.String,
                Signature = m.FullName,
                IsPublic = m.IsPublic,
                IsStatic = m.IsStatic,
                IsVirtual = m.IsVirtual,
                IsAbstract = m.IsAbstract,
                ReturnType = m.ReturnType?.FullName ?? "void",
                Parameters = m.Parameters.Select(p => new
                {
                    Name = p.Name,
                    Type = p.Type.FullName
                }).ToList()
            }).ToList();

            var fields = type.Fields.Select(f => new
            {
                Name = f.Name.String,
                Type = f.FieldType.FullName,
                IsPublic = f.IsPublic,
                IsStatic = f.IsStatic,
                IsLiteral = f.IsLiteral
            }).ToList();

            var properties = type.Properties.Select(p => new
            {
                Name = p.Name.String,
                Type = p.PropertySig?.RetType?.FullName ?? "unknown",
                CanRead = p.GetMethod != null,
                CanWrite = p.SetMethod != null
            }).ToList();

            var methodsToReturn = allMethods.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allMethods.Count;

            var info = new Dictionary<string, object>
            {
                ["FullName"] = type.FullName,
                ["Namespace"] = type.Namespace.String,
                ["Name"] = type.Name.String,
                ["IsPublic"] = type.IsPublic,
                ["IsClass"] = type.IsClass,
                ["IsInterface"] = type.IsInterface,
                ["IsEnum"] = type.IsEnum,
                ["IsValueType"] = type.IsValueType,
                ["IsAbstract"] = type.IsAbstract,
                ["IsSealed"] = type.IsSealed,
                ["BaseType"] = type.BaseType?.FullName ?? "None",
                ["Interfaces"] = type.Interfaces.Select(i => i.Interface.FullName).ToList(),
                ["Methods"] = methodsToReturn,
                ["MethodsTotalCount"] = allMethods.Count,
                ["MethodsReturnedCount"] = methodsToReturn.Count,
                ["Fields"] = fields,
                ["Properties"] = properties
            };

            if (hasMore)
            {
                info["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult DecompileMethod(Dictionary<string, object>? arguments)
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

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method == null)
                throw new ArgumentException($"Method not found: {methodName}");

            // Decompile the method
            var decompiler = decompilerService.Decompiler;
            var output = new StringBuilderDecompilerOutput();
            var decompilationContext = new DecompilationContext
            {
                CancellationToken = System.Threading.CancellationToken.None
            };

            decompiler.Decompile(method, output, decompilationContext);

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = output.ToString() }
                }
            };
        }

        CallToolResult SearchTypes(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("query", out var queryObj))
                throw new ArgumentException("query is required");

            var query = queryObj.ToString() ?? string.Empty;
            var queryLower = query.ToLowerInvariant();

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var results = documentTreeView.GetAllModuleNodes()
                .SelectMany(m => m.Document?.ModuleDef?.Types ?? Enumerable.Empty<TypeDef>())
                .Where(t => t.FullName.ToLowerInvariant().Contains(queryLower))
                .Select(t => new
                {
                    AssemblyName = t.Module?.Assembly?.Name.String ?? "Unknown",
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    IsPublic = t.IsPublic
                })
                .ToList();

            return CreatePaginatedResponse(results, offset, pageSize);
        }

        CallToolResult GenerateBepInExPlugin(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("plugin_name", out var pluginNameObj))
                throw new ArgumentException("plugin_name is required");
            if (!arguments.TryGetValue("plugin_guid", out var pluginGuidObj))
                throw new ArgumentException("plugin_guid is required");
            if (!arguments.TryGetValue("target_assembly", out var targetAssemblyObj))
                throw new ArgumentException("target_assembly is required");

            var pluginName = pluginNameObj.ToString() ?? string.Empty;
            var pluginGuid = pluginGuidObj.ToString() ?? string.Empty;
            var targetAssembly = targetAssemblyObj.ToString() ?? string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("using BepInEx;");
            sb.AppendLine("using BepInEx.Logging;");
            sb.AppendLine("using HarmonyLib;");
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine($"namespace {pluginName}");
            sb.AppendLine("{");
            sb.AppendLine($"    [BepInPlugin(\"{pluginGuid}\", \"{pluginName}\", \"1.0.0\")]");
            sb.AppendLine($"    public class {pluginName}Plugin : BaseUnityPlugin");
            sb.AppendLine("    {");
            sb.AppendLine("        private static ManualLogSource Log;");
            sb.AppendLine("        private Harmony harmony;");
            sb.AppendLine();
            sb.AppendLine("        private void Awake()");
            sb.AppendLine("        {");
            sb.AppendLine("            Log = Logger;");
            sb.AppendLine($"            Log.LogInfo(\"{pluginName} is loading...\");");
            sb.AppendLine();
            sb.AppendLine($"            harmony = new Harmony(\"{pluginGuid}\");");
            sb.AppendLine("            harmony.PatchAll();");
            sb.AppendLine();
            sb.AppendLine($"            Log.LogInfo(\"{pluginName} loaded successfully!\");");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void OnDestroy()");
            sb.AppendLine("        {");
            sb.AppendLine("            harmony?.UnpatchSelf();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // Add hooks if provided
            if (arguments.TryGetValue("hooks", out var hooksObj) && hooksObj is JsonElement hooksElement)
            {
                try
                {
                    var hooks = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(hooksElement.ToString());
                    if (hooks != null && hooks.Count > 0)
                    {
                        sb.AppendLine();
                        foreach (var hook in hooks)
                        {
                            if (hook.TryGetValue("type_name", out var typeName) &&
                                hook.TryGetValue("method_name", out var methodName))
                            {
                                sb.AppendLine();
                                sb.AppendLine($"    [HarmonyPatch(typeof({typeName}), \"{methodName}\")]");
                                sb.AppendLine($"    class {typeName.Replace(".", "_")}_{methodName}_Patch");
                                sb.AppendLine("    {");
                                sb.AppendLine("        static void Prefix()");
                                sb.AppendLine("        {");
                                sb.AppendLine($"            // Add your code before {methodName} executes");
                                sb.AppendLine("        }");
                                sb.AppendLine();
                                sb.AppendLine("        static void Postfix()");
                                sb.AppendLine("        {");
                                sb.AppendLine($"            // Add your code after {methodName} executes");
                                sb.AppendLine("        }");
                                sb.AppendLine("    }");
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore hook parsing errors
                }
            }

            sb.AppendLine("}");

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = sb.ToString() }
                }
            };
        }

        CallToolResult GetTypeFields(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("pattern", out var patternObj))
                throw new ArgumentException("pattern is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var pattern = patternObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            // Convert wildcard pattern to regex
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            var regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var allMatchingFields = type.Fields
                .Where(f => regex.IsMatch(f.Name.String))
                .Select(f => new
                {
                    Name = f.Name.String,
                    Type = f.FieldType.FullName,
                    IsPublic = f.IsPublic,
                    IsStatic = f.IsStatic,
                    IsLiteral = f.IsLiteral,
                    IsReadOnly = f.IsInitOnly,
                    Attributes = f.Attributes.ToString()
                })
                .ToList();

            var fieldsToReturn = allMatchingFields.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allMatchingFields.Count;

            var response = new Dictionary<string, object>
            {
                ["Type"] = typeFullName,
                ["Pattern"] = pattern,
                ["MatchCount"] = allMatchingFields.Count,
                ["ReturnedCount"] = fieldsToReturn.Count,
                ["Fields"] = fieldsToReturn
            };

            if (hasMore)
            {
                response["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult GetTypeProperty(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("property_name", out var propertyNameObj))
                throw new ArgumentException("property_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var propertyName = propertyNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var property = type.Properties.FirstOrDefault(p => p.Name.String.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (property == null)
                throw new ArgumentException($"Property not found: {propertyName}");

            var propertyInfo = new
            {
                Name = property.Name.String,
                Type = property.PropertySig?.RetType?.FullName ?? "unknown",
                CanRead = property.GetMethod != null,
                CanWrite = property.SetMethod != null,
                GetMethod = property.GetMethod != null ? new
                {
                    Name = property.GetMethod.Name.String,
                    IsPublic = property.GetMethod.IsPublic,
                    IsStatic = property.GetMethod.IsStatic
                } : null,
                SetMethod = property.SetMethod != null ? new
                {
                    Name = property.SetMethod.Name.String,
                    IsPublic = property.SetMethod.IsPublic,
                    IsStatic = property.SetMethod.IsStatic
                } : null,
                Attributes = property.Attributes.ToString(),
                CustomAttributes = property.CustomAttributes.Select(a => a.AttributeType.FullName).ToList()
            };

            var result = JsonSerializer.Serialize(propertyInfo, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult FindPathToType(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("from_type", out var fromTypeObj))
                throw new ArgumentException("from_type is required");
            if (!arguments.TryGetValue("to_type", out var toTypeObj))
                throw new ArgumentException("to_type is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var fromTypeName = fromTypeObj.ToString() ?? string.Empty;
            var toTypeName = toTypeObj.ToString() ?? string.Empty;

            int maxDepth = 5;
            if (arguments.TryGetValue("max_depth", out var maxDepthObj))
            {
                if (maxDepthObj is JsonElement elem && elem.TryGetInt32(out var depth))
                    maxDepth = depth;
            }

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var fromType = FindTypeInAssembly(assembly, fromTypeName);
            if (fromType == null)
                throw new ArgumentException($"From type not found: {fromTypeName}");

            // Find all types matching the target (support partial names)
            var toTypeLower = toTypeName.ToLowerInvariant();
            var targetTypes = assembly.Modules
                .SelectMany(m => m.Types)
                .Where(t => t.FullName.ToLowerInvariant().Contains(toTypeLower) ||
                            t.Name.String.ToLowerInvariant().Contains(toTypeLower))
                .ToList();

            if (targetTypes.Count == 0)
                throw new ArgumentException($"Target type not found: {toTypeName}");

            // BFS to find paths
            var paths = new List<object>();
            foreach (var targetType in targetTypes)
            {
                var path = FindPathBFS(fromType, targetType, maxDepth);
                if (path != null)
                    paths.Add(path);
            }

            if (paths.Count == 0)
            {
                return new CallToolResult
                {
                    Content = new List<ToolContent> {
                        new ToolContent { Text = $"No path found from {fromTypeName} to {toTypeName} within depth {maxDepth}" }
                    }
                };
            }

            var result = JsonSerializer.Serialize(new
            {
                FromType = fromTypeName,
                ToType = toTypeName,
                PathsFound = paths.Count,
                Paths = paths
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        object? FindPathBFS(TypeDef fromType, TypeDef toType, int maxDepth)
        {
            var queue = new Queue<(TypeDef type, List<string> path)>();
            var visited = new HashSet<string>();

            queue.Enqueue((fromType, new List<string> { fromType.Name.String }));
            visited.Add(fromType.FullName);

            while (queue.Count > 0)
            {
                var (currentType, currentPath) = queue.Dequeue();

                if (currentPath.Count > maxDepth + 1)
                    continue;

                // Check if we reached the target
                if (currentType.FullName == toType.FullName)
                {
                    return new
                    {
                        Path = string.Join(" -> ", currentPath),
                        Depth = currentPath.Count - 1,
                        Steps = currentPath
                    };
                }

                // Explore properties
                foreach (var prop in currentType.Properties)
                {
                    var propType = prop.PropertySig?.RetType?.ToTypeDefOrRef()?.ResolveTypeDef();
                    if (propType != null && !visited.Contains(propType.FullName))
                    {
                        visited.Add(propType.FullName);
                        var newPath = new List<string>(currentPath) { prop.Name.String };
                        queue.Enqueue((propType, newPath));
                    }
                }

                // Explore fields
                foreach (var field in currentType.Fields)
                {
                    var fieldType = field.FieldType?.ToTypeDefOrRef()?.ResolveTypeDef();
                    if (fieldType != null && !visited.Contains(fieldType.FullName))
                    {
                        visited.Add(fieldType.FullName);
                        var newPath = new List<string>(currentPath) { field.Name.String };
                        queue.Enqueue((fieldType, newPath));
                    }
                }
            }

            return null;
        }

        AssemblyDef? FindAssemblyByName(string name)
        {
            return documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        TypeDef? FindTypeInAssembly(AssemblyDef assembly, string fullName)
        {
            return assembly.Modules
                .SelectMany(m => m.Types)
                .FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));
        }

        /// <summary>
        /// Encodes pagination state into an opaque cursor string.
        /// </summary>
        string EncodeCursor(int offset, int pageSize)
        {
            var cursorData = new { offset, pageSize };
            var json = JsonSerializer.Serialize(cursorData);
            var bytes = Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Decodes a cursor string into pagination state.
        /// Returns (offset, pageSize) tuple. Returns (0, 100) if cursor is null/empty.
        /// Throws ArgumentException for invalid cursors (per MCP protocol, error code -32602).
        /// </summary>
        (int offset, int pageSize) DecodeCursor(string? cursor)
        {
            const int defaultPageSize = 100;

            // Null or empty cursor is valid - it's the first request
            if (string.IsNullOrEmpty(cursor))
                return (0, defaultPageSize);

            try
            {
                var bytes = Convert.FromBase64String(cursor);
                var json = Encoding.UTF8.GetString(bytes);
                var cursorData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (cursorData == null)
                    throw new ArgumentException("Invalid cursor: cursor data is null");

                if (!cursorData.TryGetValue("offset", out var offsetObj) || !(offsetObj is JsonElement offsetElem) || !offsetElem.TryGetInt32(out var offset))
                    throw new ArgumentException("Invalid cursor: missing or invalid 'offset' field");

                if (!cursorData.TryGetValue("pageSize", out var pageSizeObj) || !(pageSizeObj is JsonElement pageSizeElem) || !pageSizeElem.TryGetInt32(out var pageSize))
                    throw new ArgumentException("Invalid cursor: missing or invalid 'pageSize' field");

                if (offset < 0)
                    throw new ArgumentException("Invalid cursor: offset cannot be negative");

                if (pageSize <= 0)
                    throw new ArgumentException("Invalid cursor: pageSize must be positive");

                return (offset, pageSize);
            }
            catch (FormatException)
            {
                throw new ArgumentException("Invalid cursor: not a valid base64 string");
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"Invalid cursor: invalid JSON format - {ex.Message}");
            }
            catch (ArgumentException)
            {
                // Re-throw ArgumentExceptions we created above
                throw;
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid cursor: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a paginated response with optional nextCursor.
        /// </summary>
        CallToolResult CreatePaginatedResponse<T>(List<T> allItems, int offset, int pageSize)
        {
            var itemsToReturn = allItems.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allItems.Count;

            var response = new Dictionary<string, object>
            {
                ["items"] = itemsToReturn,
                ["total_count"] = allItems.Count,
                ["returned_count"] = itemsToReturn.Count
            };

            if (hasMore)
            {
                response["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }
    }

    /// <summary>
    /// Helper class that captures decompiler output into a string.
    /// </summary>
    class StringBuilderDecompilerOutput : IDecompilerOutput
    {
        readonly StringBuilder sb = new StringBuilder();

        public void Write(string text, object color) => sb.Append(text);
        public void WriteLine() => sb.AppendLine();

        public override string ToString() => sb.ToString();

        public int Length => sb.Length;
        public int NextPosition => sb.Length;
        public bool UsesCustomData => false;

        public void AddCustomData<TData>(string id, TData data) { }
        public void DecreaseIndent() { }
        public void IncreaseIndent() { }
        public void Write(string text, int index, int length, object color) => sb.Append(text, index, length);
        public void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) => sb.Append(text);
        public void Write(string text, int index, int length, object? reference, DecompilerReferenceFlags flags, object color) => sb.Append(text, index, length);
    }
}
