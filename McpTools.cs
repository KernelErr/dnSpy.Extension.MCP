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
    sealed partial class McpTools
    {
        readonly IDocumentTreeView documentTreeView;
        readonly IDecompilerService decompilerService;
        readonly McpSettings settings;

        /// <summary>
        /// Initializes the MCP tools with dnSpy services.
        /// </summary>
        [ImportingConstructor]
        public McpTools(IDocumentTreeView documentTreeView, IDecompilerService decompilerService, McpSettings settings)
        {
            this.documentTreeView = documentTreeView;
            this.decompilerService = decompilerService;
            this.settings = settings;
        }

        /// <summary>
        /// Gets the list of available MCP tools with their schemas.
        /// </summary>
        public List<ToolInfo> GetAvailableTools()
        {
            return new List<ToolInfo> {
                new ToolInfo {
                    Name = "list_assemblies",
                    Description = "List all loaded assemblies in dnSpy. Unity titles load hundreds of framework modules; pass name_filter (substring or '*' wildcard) to narrow, e.g. 'Assembly-CSharp'.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["name_filter"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional. Case-insensitive substring, or '*' wildcard anchored to the whole name (e.g. 'Assembly-CSharp', '*Firstpass')."
                            }
                        },
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "get_assembly_info",
                    Description = "Get detailed information about a specific assembly. Supports pagination of namespaces with default page size of 10.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination of namespaces (opaque token from previous response). Default page size: 10 namespaces."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "list_types",
                    Description = "List all types in an assembly or namespace. Paginated (default page size 10; override with page_size). Use names_only for a compact list of FullName strings, and base_type to list only (transitive) subclasses of a base — e.g. base_type='MonoBehaviour' lists all Unity components.",
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
                            ["base_type"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional. Only include types whose base chain contains this (case-insensitive substring of a base type FullName), e.g. 'MonoBehaviour', 'UnityEngine.MonoBehaviour', 'ScriptableObject'."
                            },
                            ["names_only"] = new Dictionary<string, object> {
                                ["type"] = "boolean",
                                ["description"] = "Default false. Return a flat list of type FullName strings instead of per-type metadata — much cheaper in tokens."
                            },
                            ["page_size"] = new Dictionary<string, object> {
                                ["type"] = "integer",
                                ["description"] = "Optional page size for the first request (default 10, max 1000). Follow-up pages keep it via the cursor."
                            },
                            ["include_nested"] = new Dictionary<string, object> {
                                ["type"] = "boolean",
                                ["description"] = "Default true. Include nested types — including compiler-generated async/iterator state machines (e.g. GameOver/<Awake>d__63). Set false for top-level types only. Each entry carries is_nested / is_compiler_generated / declaring_type."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response)."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "get_type_info",
                    Description = "Get detailed information about a specific type including its members. First request returns all fields/properties and paginated methods. Subsequent requests (with cursor) return only paginated methods. Use compact=true to drop per-member detail (name+signature+token only) and members_filter to return only members whose name matches a pattern (e.g. '*Save*') — both cut token cost sharply on large types.",
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
                            ["compact"] = new Dictionary<string, object> {
                                ["type"] = "boolean",
                                ["description"] = "Default false. Return only name + signature + token per method, and name + type per field/property. Much cheaper than the full per-member detail."
                            },
                            ["members_filter"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional. Only include methods/fields/properties whose name matches (case-insensitive substring, or '*' wildcard anchored to the whole name), e.g. '*Save*'."
                            },
                            ["page_size"] = new Dictionary<string, object> {
                                ["type"] = "integer",
                                ["description"] = "Optional page size for methods on the first request (default 10, max 1000)."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination of methods (opaque token from previous response)."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "decompile_method",
                    Description = "Decompile a specific method to C# code. For overloaded methods, pass parameter_types (array of fully-qualified type names from list_methods) or method_token (uint MDToken) to disambiguate.",
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
                            },
                            ["parameter_types"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                                ["description"] = "Optional. Fully-qualified parameter type names (e.g. [\"System.Int32\",\"System.String\"]) to disambiguate overloads. Matches MethodSig.Params, not including 'this'."
                            },
                            ["method_token"] = new Dictionary<string, object> {
                                ["type"] = "integer",
                                ["description"] = "Optional. MDToken.Raw as uint (from get_type_info or list_methods). Unambiguous — takes precedence over parameter_types."
                            },
                            ["include_state_machine"] = new Dictionary<string, object> {
                                ["type"] = "boolean",
                                ["description"] = "Default true. For async / iterator methods, if the decompiler can't inline the state machine back into await/yield (common on Unity/Mono output), append the raw compiler-generated MoveNext body so the real logic isn't lost. Set false to get only the kickoff."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "search_types",
                    Description = "Search for types by name. Defaults to all loaded assemblies — pass assembly_name to scope to one (e.g. 'Assembly-CSharp') so framework types don't drown game types. Matches nested compiler-generated types too. Paginated (default page size 10; override with page_size). Use names_only for a compact FullName-string list.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["query"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Search query. Wildcards (*) match against FullName (namespace + type name). Recommended patterns: '*TypeName' for suffix (e.g., '*Controller' finds MyNamespace.PlayerController), '*.Keyword*' for types containing keyword, 'Full.Namespace.Path.*' for specific namespace. Without wildcards, performs case-insensitive substring matching (e.g., 'Controller' finds all types with 'Controller' in name)."
                            },
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional. Restrict the search to a single assembly. Omit to search all loaded assemblies."
                            },
                            ["names_only"] = new Dictionary<string, object> {
                                ["type"] = "boolean",
                                ["description"] = "Default false. Return a flat list of type FullName strings instead of per-type metadata — much cheaper in tokens."
                            },
                            ["page_size"] = new Dictionary<string, object> {
                                ["type"] = "integer",
                                ["description"] = "Optional page size for the first request (default 10, max 1000)."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response)."
                            }
                        },
                        ["required"] = new List<string> { "query" }
                    }
                },
                new ToolInfo {
                    Name = "decompile_by_token",
                    Description = "Decompile a method (or type) to C# by MDToken alone — no type name needed. Ideal when you have a token from find_callers / find_references / search_string_literals / list_methods but not the declaring type (also the simplest way to reach nested compiler-generated state machines). Tokens are per-module: pass assembly_name to be exact; without it the token must be unambiguous across loaded assemblies. Method tokens get the same async/iterator state-machine rescue as decompile_method.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["token"] = new Dictionary<string, object> {
                                ["type"] = "integer",
                                ["description"] = "MDToken.Raw as uint (e.g. the method_token / caller_token returned by other tools)."
                            },
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional. Module the token belongs to. Recommended — tokens are only unique within a module."
                            },
                            ["include_state_machine"] = new Dictionary<string, object> {
                                ["type"] = "boolean",
                                ["description"] = "Default true. For async/iterator methods, append the raw MoveNext body when the kickoff can't be reconstructed (same behavior as decompile_method)."
                            }
                        },
                        ["required"] = new List<string> { "token" }
                    }
                },
                new ToolInfo {
                    Name = "find_callers",
                    Description = "Cross-reference: find every method that CALLS a given method (call / callvirt / newobj / ldftn / ldvirtftn), across ALL loaded assemblies — callers routinely live in a different assembly than the target. Identify the target by assembly_name + type_full_name + method_name (with parameter_types or method_token to disambiguate overloads). Each hit returns the caller's type, method, MDToken (uint), signature, the call opcode, the resolved reference, and IL index/offset. Paginated (default page size 10).",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Assembly that declares the target method"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type that declares the target method"
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the target method"
                            },
                            ["parameter_types"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                                ["description"] = "Optional. Fully-qualified parameter type names to disambiguate an overloaded target."
                            },
                            ["method_token"] = new Dictionary<string, object> {
                                ["type"] = "integer",
                                ["description"] = "Optional. MDToken.Raw (uint) of the target method. Takes precedence over parameter_types."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_references",
                    Description = "Cross-reference: find every IL site that references a target across ALL loaded assemblies. target_kind selects what to look for: 'method' (any call / ldftn / ldtoken of the method), 'field' (any ldfld/stfld/ldsfld/stsfld/ldflda + ldtoken), 'type' (newarr / castclass / isinst / box / ldtoken / etc.), or 'string' (ldstr matching a query). Provide the identity fields for the chosen kind (see properties). Each hit returns the referencing type, method, MDToken (uint), signature, opcode, the resolved reference, and IL index/offset. Paginated (default page size 10). For methods, find_callers is the call-only specialization.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["target_kind"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["enum"] = new List<string> { "method", "field", "type", "string" },
                                ["description"] = "What to look for: method | field | type | string."
                            },
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Assembly declaring the target (required for method/field/type)."
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the target type (for target_kind=type) or the type declaring the target member (method/field)."
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Target method name (target_kind=method)."
                            },
                            ["parameter_types"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                                ["description"] = "Optional. Disambiguate an overloaded target method."
                            },
                            ["method_token"] = new Dictionary<string, object> {
                                ["type"] = "integer",
                                ["description"] = "Optional. MDToken.Raw (uint) of the target method. Takes precedence over parameter_types."
                            },
                            ["field_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Target field name (target_kind=field)."
                            },
                            ["query"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "String to match (target_kind=string). Case-insensitive substring, or '*' wildcard anchored to the whole literal."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
                            }
                        },
                        ["required"] = new List<string> { "target_kind" }
                    }
                },
                new ToolInfo {
                    Name = "search_string_literals",
                    Description = "Reverse-lookup: find every method that emits a given string literal (ldstr) across loaded assemblies. Answers \"which method uses this string?\" — the #1 question in game/Unity RE where logic is wired by string keys (PlayerPrefs keys, scene names, save tokens). Query is a case-insensitive substring by default; use * for wildcards anchored to the whole string (e.g. 'SAVE*'). Optionally restrict to one assembly. Each hit returns the string value, declaring type, method name + MDToken (uint), full signature, and IL index/offset. Paginated (default page size 10).",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["query"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "String to search for. Case-insensitive substring by default; '*' is a wildcard matching the whole literal (e.g. 'Player*Score', '*SAVEFILE*')."
                            },
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional. Restrict the search to a single assembly. Omit to sweep all loaded assemblies."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
                            }
                        },
                        ["required"] = new List<string> { "query" }
                    }
                },
                new ToolInfo {
                    Name = "list_string_constants",
                    Description = "List all string literals (ldstr) in a type, or in a single method. Type scope includes nested types (compiler-generated closures often hold the interesting keys). Pass method_name (with parameter_types or method_token to disambiguate overloads) to narrow to one method. Each entry returns the string value, declaring type, method name + MDToken (uint), full signature, and IL index/offset. Paginated (default page size 10).",
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
                                ["description"] = "Optional. Restrict to a single method. If omitted, lists ldstr across the type and its nested types."
                            },
                            ["parameter_types"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                                ["description"] = "Optional. Fully-qualified parameter type names to disambiguate an overloaded method_name."
                            },
                            ["method_token"] = new Dictionary<string, object> {
                                ["type"] = "integer",
                                ["description"] = "Optional. MDToken.Raw (uint) to disambiguate an overloaded method_name. Takes precedence over parameter_types."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
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
                    Description = "Get fields from a type matching a name pattern (supports wildcards like *Bonus*). Supports pagination with default page size of 10 fields.",
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
                                ["description"] = "Optional cursor for pagination of fields (opaque token from previous response). Default page size: 10 fields."
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
                },
                new ToolInfo {
                    Name = "list_methods",
                    Description = "List methods of a type with unambiguous identifiers. Each entry includes the MDToken (as uint) and parameter_types array so the caller can feed either back into get_method_il / patch_method_il / decompile_method to disambiguate overloads. Paginated (default page size 10).",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Fully qualified type name"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Opaque pagination cursor from a previous response."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "get_method_il",
                    Description = "Return the IL body of a method: instructions (index, offset, opcode, operand), locals, exception handlers, and body flags. Operand format is tagged and round-trips with patch_method_il (e.g. 'int:42', 'str:\"hello\"', 'method:Ns.T::M(System.Int32):System.Void', 'label:7'). For overloaded methods, pass parameter_types or method_token to disambiguate.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Fully qualified type name"
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Method name"
                            },
                            ["parameter_types"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                                ["description"] = "Optional. Fully-qualified parameter type names for overload disambiguation."
                            },
                            ["method_token"] = new Dictionary<string, object> {
                                ["type"] = "integer",
                                ["description"] = "Optional. MDToken.Raw from list_methods / get_type_info. Takes precedence over parameter_types."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "patch_method_il",
                    Description = "Apply an ordered list of IL edits to a method body in memory. Ops: {op:\"replace\",index,opcode,operand}, {op:\"insert\",index,opcode,operand} (insert BEFORE index), {op:\"delete\",index}, {op:\"set_init_locals\",value:bool}. Indices in later ops refer to the state BEFORE the whole batch. Operand grammar matches get_method_il output. Set optimize_macros=true to auto-shorten after edits. Changes are NOT written to disk — call save_assembly to persist. First patch to a method is snapshotted so revert_method_il can restore it.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["method_name"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["parameter_types"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object> { ["type"] = "string" }
                            },
                            ["method_token"] = new Dictionary<string, object> { ["type"] = "integer" },
                            ["edits"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object> {
                                    ["type"] = "object",
                                    ["oneOf"] = new List<object> {
                                        new Dictionary<string, object> {
                                            ["properties"] = new Dictionary<string, object> {
                                                ["op"] = new Dictionary<string, object> { ["const"] = "replace" },
                                                ["index"] = new Dictionary<string, object> { ["type"] = "integer" },
                                                ["opcode"] = new Dictionary<string, object> { ["type"] = "string" },
                                                ["operand"] = new Dictionary<string, object> { ["type"] = "string" }
                                            },
                                            ["required"] = new List<string> { "op", "index", "opcode", "operand" },
                                            ["additionalProperties"] = false
                                        },
                                        new Dictionary<string, object> {
                                            ["properties"] = new Dictionary<string, object> {
                                                ["op"] = new Dictionary<string, object> { ["const"] = "insert" },
                                                ["index"] = new Dictionary<string, object> { ["type"] = "integer" },
                                                ["opcode"] = new Dictionary<string, object> { ["type"] = "string" },
                                                ["operand"] = new Dictionary<string, object> { ["type"] = "string" }
                                            },
                                            ["required"] = new List<string> { "op", "index", "opcode", "operand" },
                                            ["additionalProperties"] = false
                                        },
                                        new Dictionary<string, object> {
                                            ["properties"] = new Dictionary<string, object> {
                                                ["op"] = new Dictionary<string, object> { ["const"] = "delete" },
                                                ["index"] = new Dictionary<string, object> { ["type"] = "integer" }
                                            },
                                            ["required"] = new List<string> { "op", "index" },
                                            ["additionalProperties"] = false
                                        },
                                        new Dictionary<string, object> {
                                            ["properties"] = new Dictionary<string, object> {
                                                ["op"] = new Dictionary<string, object> { ["const"] = "set_init_locals" },
                                                ["value"] = new Dictionary<string, object> { ["type"] = "boolean" }
                                            },
                                            ["required"] = new List<string> { "op", "value" },
                                            ["additionalProperties"] = false
                                        }
                                    }
                                },
                                ["description"] = "Ordered edit ops. See tool description for shape."
                            },
                            ["optimize_macros"] = new Dictionary<string, object> {
                                ["type"] = "boolean",
                                ["description"] = "Call body.OptimizeMacros() after edits to auto-shorten ldarg/ldloc/branches. Default false."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name", "edits" }
                    }
                },
                new ToolInfo {
                    Name = "revert_method_il",
                    Description = "Restore the method body that was captured on first patch_method_il. Fails with -32602 if no pending snapshot exists for the method.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["method_name"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["parameter_types"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object> { ["type"] = "string" }
                            },
                            ["method_token"] = new Dictionary<string, object> { ["type"] = "integer" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "save_assembly",
                    Description = "Write the (possibly patched) module of an assembly back to disk. When output_path is omitted, the original file is overwritten after a timestamped backup (<path>.<yyyyMMdd-HHmmss>.bak) is created. GAC paths are refused. Memory-mapped I/O is disabled before writing so the live dnSpy process releases the file. Note: dnSpy's in-memory tree is NOT refreshed — reopen the assembly to see the saved state inside this instance.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly to save"
                            },
                            ["output_path"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional. Target file path. If absent, overwrite original with a timestamped backup."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
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
            // Marshal every tool handler onto the WPF UI thread. dnSpy's document tree
            // is a DispatcherObject — the moment a user-loaded assembly is indexed, all
            // downstream tree/node reads from an HTTP worker thread throw
            // "calling thread cannot access this object". Patch/save already take this
            // path explicitly; InvokeOnUiThread short-circuits when already on the UI
            // thread, so the double-wrap is a no-op.
            return InvokeOnUiThread(() =>
            {
                try
                {
                    return toolName switch
                    {
                        "list_assemblies" => ListAssemblies(arguments),
                        "get_assembly_info" => GetAssemblyInfo(arguments),
                        "list_types" => ListTypes(arguments),
                        "get_type_info" => GetTypeInfo(arguments),
                        "decompile_method" => DecompileMethod(arguments),
                        "decompile_by_token" => DecompileByToken(arguments),
                        "search_types" => SearchTypes(arguments),
                        "find_callers" => FindCallers(arguments),
                        "find_references" => FindReferences(arguments),
                        "search_string_literals" => SearchStringLiterals(arguments),
                        "list_string_constants" => ListStringConstants(arguments),
                        "generate_bepinex_plugin" => GenerateBepInExPlugin(arguments),
                        "get_type_fields" => GetTypeFields(arguments),
                        "get_type_property" => GetTypeProperty(arguments),
                        "find_path_to_type" => FindPathToType(arguments),
                        "list_methods" => ListMethods(arguments),
                        "get_method_il" => GetMethodIL(arguments),
                        "patch_method_il" => PatchMethodIL(arguments),
                        "revert_method_il" => RevertMethodIL(arguments),
                        "save_assembly" => SaveAssembly(arguments),
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
            });
        }

        CallToolResult ListAssemblies(Dictionary<string, object>? arguments)
        {
            // Optional name filter: Unity titles load hundreds of framework modules; a dev usually
            // only cares about Assembly-CSharp. Substring (case-insensitive) or '*' wildcard.
            Func<string, bool> nameMatch = _ => true;
            if (arguments != null && arguments.TryGetValue("name_filter", out var nfObj) && nfObj != null)
            {
                var nf = nfObj.ToString();
                if (!string.IsNullOrWhiteSpace(nf))
                    nameMatch = BuildStringMatcher(nf!);
            }

            var assemblies = documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .Where(a => a != null)
                .Distinct()
                .Where(a => nameMatch(a!.Name.String))
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

            // Default to including nested types so compiler-generated state machines (async /
            // iterator) are listed — they're where Unity coroutine / async logic actually lives.
            var includeNested = ReadOptionalBool(arguments, "include_nested") ?? true;
            var namesOnly = ReadOptionalBool(arguments, "names_only") ?? false;
            var baseTypeFilter = ReadOptionalString(arguments, "base_type");

            var (offset, pageSize) = ResolvePageSize(cursor, arguments);

            var allTypes = (includeNested
                    ? assembly.Modules.SelectMany(m => m.GetTypes())
                    : assembly.Modules.SelectMany(m => m.Types))
                .Where(t => string.IsNullOrEmpty(namespaceFilter) || t.Namespace == namespaceFilter)
                .Where(t => baseTypeFilter == null || InheritsFrom(t, baseTypeFilter));

            // names_only is the compact mode: a flat list of FullName strings instead of per-type
            // metadata objects — far cheaper in tokens when you just want to scan 100+ type names.
            if (namesOnly)
                return CreatePaginatedResponse(allTypes.Select(t => t.FullName).ToList(), offset, pageSize);

            var types = allTypes
                .Select(t => new
                {
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    IsPublic = t.IsPublic,
                    IsNested = t.IsNested,
                    IsCompilerGenerated = IsCompilerGeneratedType(t),
                    DeclaringType = t.DeclaringType?.FullName,
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

            var (offset, pageSize) = ResolvePageSize(cursor, arguments);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            // compact: drop per-member detail (params/flags) and keep just name+signature+token —
            // GetTypeInfo on a 60-field / many-method type is otherwise very heavy in tokens.
            // members_filter: name pattern (substring or '*' wildcard) applied to methods/fields/
            // properties so you can ask for just *Save* members instead of the whole type.
            var compact = ReadOptionalBool(arguments, "compact") ?? false;
            var memberFilterRaw = ReadOptionalString(arguments, "members_filter");
            var memberMatch = memberFilterRaw == null ? (_ => true) : BuildStringMatcher(memberFilterRaw);

            var allMethods = type.Methods.Where(m => memberMatch(m.Name.String)).Select(m => compact
                ? (object)new
                {
                    Name = m.Name.String,
                    Token = m.MDToken.Raw,
                    Signature = m.FullName
                }
                : new
                {
                    Name = m.Name.String,
                    Token = m.MDToken.Raw,
                    Signature = m.FullName,
                    IsPublic = m.IsPublic,
                    IsStatic = m.IsStatic,
                    IsVirtual = m.IsVirtual,
                    IsAbstract = m.IsAbstract,
                    ReturnType = m.ReturnType?.FullName ?? "void",
                    ParameterTypes = m.MethodSig == null ? new List<string>() : m.MethodSig.Params.Select(t => t?.FullName ?? "?").ToList(),
                    Parameters = m.Parameters.Select(p => new
                    {
                        Name = p.Name,
                        Type = p.Type.FullName
                    }).ToList()
                }).ToList();

            var fields = type.Fields.Where(f => memberMatch(f.Name.String)).Select(f => compact
                ? (object)new { Name = f.Name.String, Type = f.FieldType.FullName }
                : new
                {
                    Name = f.Name.String,
                    Type = f.FieldType.FullName,
                    IsPublic = f.IsPublic,
                    IsStatic = f.IsStatic,
                    IsLiteral = f.IsLiteral
                }).ToList();

            var properties = type.Properties.Where(p => memberMatch(p.Name.String)).Select(p => compact
                ? (object)new { Name = p.Name.String, Type = p.PropertySig?.RetType?.FullName ?? "unknown" }
                : new
                {
                    Name = p.Name.String,
                    Type = p.PropertySig?.RetType?.FullName ?? "unknown",
                    CanRead = p.GetMethod != null,
                    CanWrite = p.SetMethod != null
                }).ToList();

            var methodsToReturn = allMethods.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allMethods.Count;
            var isFirstRequest = string.IsNullOrEmpty(cursor);

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
                ["MethodsReturnedCount"] = methodsToReturn.Count
            };

            // Only include fields and properties on first request to reduce token usage
            // For subsequent paginated requests, only return methods
            if (isFirstRequest)
            {
                info["Fields"] = fields;
                info["FieldsCount"] = fields.Count;
                info["Properties"] = properties;
                info["PropertiesCount"] = properties.Count;
            }
            else
            {
                // For paginated requests, just include counts
                info["FieldsCount"] = fields.Count;
                info["PropertiesCount"] = properties.Count;
            }

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

            var parameterTypes = ReadStringArray(arguments, "parameter_types");
            uint? methodToken = ReadOptionalUInt(arguments, "method_token");

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var method = FindMethod(type, methodName, parameterTypes, methodToken);
            var includeStateMachine = ReadOptionalBool(arguments, "include_state_machine") ?? true;

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = DecompileMethodToText(method, includeStateMachine) }
                }
            };
        }

        CallToolResult DecompileByToken(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            var token = ReadOptionalUInt(arguments, "token")
                ?? throw new ArgumentException("token is required (MDToken.Raw as uint, e.g. from find_callers / search_string_literals / list_methods)");
            var includeStateMachine = ReadOptionalBool(arguments, "include_state_machine") ?? true;

            string? assemblyName = null;
            if (arguments.TryGetValue("assembly_name", out var asmObj) && asmObj != null)
            {
                assemblyName = asmObj.ToString();
                if (string.IsNullOrWhiteSpace(assemblyName))
                    assemblyName = null;
            }

            // Tokens are per-module. Resolve in the named assembly if given, else sweep every loaded
            // module — our xref / string tools return (assembly, token) pairs, so the caller usually
            // has the assembly, but token-only still works as long as it's unambiguous.
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

            var hits = new List<(ModuleDef module, object resolved)>();
            foreach (var module in modules)
            {
                if (module is not ModuleDefMD md)
                    continue;
                object? resolved = null;
                try { resolved = md.ResolveToken(token); } catch { /* not a valid token in this module */ }
                if (resolved is MethodDef || resolved is TypeDef)
                    hits.Add((module, resolved));
            }

            if (hits.Count == 0)
                throw new ArgumentException($"No method or type with MDToken 0x{token:X8} in {(assemblyName ?? "any loaded assembly")}. Pass assembly_name if the token came from a specific module.");
            if (hits.Count > 1)
                throw new ArgumentException(
                    $"MDToken 0x{token:X8} is ambiguous across {hits.Count} assemblies ({string.Join(", ", hits.Select(h => h.module.Assembly?.Name.String ?? "?"))}). Pass assembly_name to disambiguate.");

            var (_, target) = hits[0];
            string text;
            if (target is MethodDef methodDef)
            {
                if (!methodDef.HasBody)
                    throw new ArgumentException($"Method {DescribeSignature(methodDef)} has no IL body (abstract / extern / P/Invoke).");
                text = DecompileMethodToText(methodDef, includeStateMachine);
            }
            else
            {
                var typeDef = (TypeDef)target;
                var decompiler = decompilerService.Decompiler;
                var output = new StringBuilderDecompilerOutput();
                decompiler.Decompile(typeDef, output, new DecompilationContext { CancellationToken = System.Threading.CancellationToken.None });
                text = output.ToString();
            }

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = text }
                }
            };
        }

        /// <summary>
        /// Decompiles a method to C#, with the async/iterator rescue: the compiler stamps the kickoff
        /// with [AsyncStateMachine(typeof(&lt;M&gt;d__N))] / [IteratorStateMachine(...)] pointing at the
        /// nested state machine. ILSpy normally inlines it back into await/yield, but on some compiler
        /// output (notably Unity's) the pattern match misses and we get only the kickoff stub. When that
        /// happens, append the raw MoveNext decompilation so the real logic is never lost. Skipped when
        /// the kickoff already reconstructed cleanly, or when <paramref name="includeStateMachine"/> is false.
        /// </summary>
        string DecompileMethodToText(MethodDef method, bool includeStateMachine)
        {
            var decompiler = decompilerService.Decompiler;
            var output = new StringBuilderDecompilerOutput();
            var decompilationContext = new DecompilationContext
            {
                CancellationToken = System.Threading.CancellationToken.None
            };

            decompiler.Decompile(method, output, decompilationContext);
            var text = output.ToString();

            if (includeStateMachine)
            {
                var smType = GetStateMachineType(method);
                var moveNext = smType?.Methods.FirstOrDefault(m => m.Name == "MoveNext" && m.HasBody);
                bool alreadyReconstructed = text.Contains("await ") || text.Contains("yield return") || text.Contains("yield break");
                if (moveNext != null && !alreadyReconstructed)
                {
                    var smOutput = new StringBuilderDecompilerOutput();
                    decompiler.Decompile(moveNext, smOutput, decompilationContext);
                    text += "\n\n// ===================================================================\n" +
                            $"// Kickoff reconstruction unavailable for this compiler's output.\n" +
                            $"// Raw compiler-generated state machine body: {smType!.FullName}::MoveNext\n" +
                            "// ===================================================================\n" +
                            smOutput.ToString();
                }
            }

            return text;
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

            var namesOnly = ReadOptionalBool(arguments, "names_only") ?? false;
            var (offset, pageSize) = ResolvePageSize(cursor, arguments);

            // Optional assembly scope: searching *Save* / *Storage* across all assemblies drowns
            // game types under mscorlib/System framework hits. Restrict to one assembly when given.
            var assemblyName = ReadOptionalString(arguments, "assembly_name");
            IEnumerable<TypeDef> typeUniverse;
            if (assemblyName != null)
            {
                var assembly = FindAssemblyByName(assemblyName);
                if (assembly == null)
                    throw new ArgumentException($"Assembly not found: {assemblyName}");
                typeUniverse = assembly.Modules.SelectMany(m => m.GetTypes());
            }
            else
            {
                typeUniverse = documentTreeView.GetAllModuleNodes()
                    .SelectMany(m => m.Document?.ModuleDef?.GetTypes() ?? Enumerable.Empty<TypeDef>());
            }

            // Check if query contains wildcards
            bool hasWildcard = query.Contains("*");
            System.Text.RegularExpressions.Regex? regex = null;

            if (hasWildcard)
            {
                // Convert wildcard pattern to regex
                var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(queryLower).Replace("\\*", ".*") + "$";
                regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            // GetTypes() (recursive) so nested compiler-generated state machines match too —
            // e.g. searching '*d__*' or '*<Awake>*' surfaces async / iterator state machines.
            var matched = typeUniverse
                .Where(t => hasWildcard ? regex!.IsMatch(t.FullName) : t.FullName.ToLowerInvariant().Contains(queryLower));

            if (namesOnly)
                return CreatePaginatedResponse(matched.Select(t => t.FullName).ToList(), offset, pageSize);

            var results = matched
                .Select(t => new
                {
                    AssemblyName = t.Module?.Assembly?.Name.String ?? "Unknown",
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    IsPublic = t.IsPublic,
                    IsNested = t.IsNested,
                    IsCompilerGenerated = IsCompilerGeneratedType(t),
                    DeclaringType = t.DeclaringType?.FullName
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
            // GetTypes() (not Types) walks nested types too, so compiler-generated state machines
            // like GameOver/<Awake>d__63 are addressable — the whole point of the Unity-coroutine /
            // async fix. dnlib renders nested FullNames with '/'; callers often type '.' or '+'
            // instead, so we exact-match first, then fall back to a separator-normalized compare.
            var all = assembly.Modules.SelectMany(m => m.GetTypes()).ToList();
            var exact = all.FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));
            if (exact != null)
                return exact;

            var needle = NormalizeTypeSeparators(fullName);
            return all.FirstOrDefault(t => NormalizeTypeSeparators(t.FullName).Equals(needle, StringComparison.Ordinal));
        }

        // Collapse the three nested-type separators a caller might use ('/', '+', '.') so
        // "GameOver/<Awake>d__63", "GameOver+<Awake>d__63" and "GameOver.<Awake>d__63" all resolve.
        static string NormalizeTypeSeparators(string fullName)
            => fullName.Replace('+', '/').Replace('.', '/');

        /// <summary>
        /// If <paramref name="method"/> is an async or iterator kickoff, returns the compiler-generated
        /// state machine type it points at via [AsyncStateMachine] / [IteratorStateMachine]; otherwise null.
        /// </summary>
        static TypeDef? GetStateMachineType(MethodDef method)
        {
            foreach (var ca in method.CustomAttributes)
            {
                var fn = ca.AttributeType?.FullName;
                if (fn != "System.Runtime.CompilerServices.AsyncStateMachineAttribute" &&
                    fn != "System.Runtime.CompilerServices.IteratorStateMachineAttribute")
                    continue;
                if (ca.ConstructorArguments.Count == 0)
                    continue;
                var val = ca.ConstructorArguments[0].Value;
                if (val is TypeSig ts)
                    return ts.ToTypeDefOrRef()?.ResolveTypeDef();
                if (val is ITypeDefOrRef tdr)
                    return tdr.ResolveTypeDef();
            }
            return null;
        }

        static bool IsCompilerGeneratedType(TypeDef t)
        {
            // State machines / closures are named like <Method>d__N / <>c / <>c__DisplayClass.
            if (t.Name.String.IndexOf('<') >= 0)
                return true;
            return t.CustomAttributes.Any(ca =>
                ca.AttributeType?.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        /// <summary>
        /// Resolves a method by name, optionally disambiguated by parameter types or MDToken.
        /// Resolution order: token &gt; parameter_types &gt; name-only.
        /// Throws ArgumentException (-> JSON-RPC -32602) on 0 or &gt;1 matches, with a candidate list
        /// when multiple overloads share the name so the caller can retry with parameter_types.
        /// </summary>
        MethodDef FindMethod(TypeDef type, string methodName, IList<string>? parameterTypes, uint? methodToken)
        {
            var candidates = type.Methods.Where(m => m.Name == methodName).ToList();
            if (candidates.Count == 0)
                throw new ArgumentException($"Method not found: {type.FullName}::{methodName}");

            if (methodToken.HasValue)
            {
                var byToken = candidates.FirstOrDefault(m => m.MDToken.Raw == methodToken.Value)
                    ?? type.Methods.FirstOrDefault(m => m.MDToken.Raw == methodToken.Value);
                if (byToken == null)
                    throw new ArgumentException($"No method in {type.FullName} has MDToken 0x{methodToken.Value:X8}");
                return byToken;
            }

            if (parameterTypes != null)
            {
                var matches = candidates.Where(m => MethodParamTypesMatch(m, parameterTypes)).ToList();
                if (matches.Count == 1)
                    return matches[0];
                if (matches.Count == 0)
                    throw new ArgumentException(
                        $"No overload of {type.FullName}::{methodName} has parameters [{string.Join(", ", parameterTypes)}]. " +
                        $"Candidates: {string.Join(" | ", candidates.Select(DescribeSignature))}");
                throw new ArgumentException(
                    $"Ambiguous match for {type.FullName}::{methodName}: {matches.Count} overloads matched. " +
                    $"Use method_token instead. Matches: {string.Join(" | ", matches.Select(DescribeSignature))}");
            }

            if (candidates.Count > 1)
                throw new ArgumentException(
                    $"{type.FullName}::{methodName} is overloaded ({candidates.Count}). Pass parameter_types or method_token. " +
                    $"Candidates: {string.Join(" | ", candidates.Select(DescribeSignature))}");

            return candidates[0];
        }

        static bool MethodParamTypesMatch(MethodDef m, IList<string> expected)
        {
            var sig = m.MethodSig;
            if (sig == null)
                return expected.Count == 0;
            if (sig.Params.Count != expected.Count)
                return false;
            for (int i = 0; i < expected.Count; i++)
            {
                var actual = sig.Params[i]?.FullName ?? string.Empty;
                if (!string.Equals(actual, expected[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        static string DescribeSignature(MethodDef m)
        {
            var sig = m.MethodSig;
            var @params = sig == null ? string.Empty : string.Join(",", sig.Params.Select(t => t?.FullName ?? "?"));
            var ret = m.ReturnType?.FullName ?? "void";
            return $"{m.Name}({@params}):{ret}#0x{m.MDToken.Raw:X8}";
        }

        static IList<string>? ReadStringArray(Dictionary<string, object> args, string key)
        {
            if (!args.TryGetValue(key, out var raw) || raw == null)
                return null;
            if (raw is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Null)
                    return null;
                if (el.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException($"{key} must be an array of strings");
                var list = new List<string>(el.GetArrayLength());
                foreach (var item in el.EnumerateArray())
                    list.Add(item.ValueKind == JsonValueKind.String ? (item.GetString() ?? string.Empty) : item.ToString());
                return list;
            }
            if (raw is IEnumerable<object> seq)
                return seq.Select(o => o?.ToString() ?? string.Empty).ToList();
            throw new ArgumentException($"{key} must be an array of strings");
        }

        static uint? ReadOptionalUInt(Dictionary<string, object> args, string key)
        {
            if (!args.TryGetValue(key, out var raw) || raw == null)
                return null;
            if (raw is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Null)
                    return null;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out var u))
                    return u;
                if (el.ValueKind == JsonValueKind.String && uint.TryParse(el.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var su))
                    return su;
                throw new ArgumentException($"{key} must be an unsigned 32-bit integer");
            }
            if (raw is string s && uint.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ps))
                return ps;
            if (raw is IConvertible conv)
                return Convert.ToUInt32(conv, System.Globalization.CultureInfo.InvariantCulture);
            throw new ArgumentException($"{key} must be an unsigned 32-bit integer");
        }

        static string? ReadOptionalString(Dictionary<string, object> args, string key)
        {
            if (!args.TryGetValue(key, out var raw) || raw == null)
                return null;
            string? s;
            if (raw is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Null)
                    return null;
                s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
            }
            else
            {
                s = raw.ToString();
            }
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        static int? ReadOptionalInt(Dictionary<string, object> args, string key)
        {
            if (!args.TryGetValue(key, out var raw) || raw == null)
                return null;
            if (raw is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Null)
                    return null;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
                    return n;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var sn))
                    return sn;
                throw new ArgumentException($"{key} must be an integer");
            }
            if (raw is int i)
                return i;
            if (int.TryParse(raw.ToString(), out var pi))
                return pi;
            throw new ArgumentException($"{key} must be an integer");
        }

        /// <summary>
        /// (offset, pageSize) from the cursor, with a first-request <c>page_size</c> override (capped
        /// at 1000). On paginated follow-ups the cursor's own page size is authoritative.
        /// </summary>
        (int offset, int pageSize) ResolvePageSize(string? cursor, Dictionary<string, object>? arguments)
        {
            var (offset, pageSize) = DecodeCursor(cursor);
            if (string.IsNullOrEmpty(cursor) && arguments != null)
            {
                var ps = ReadOptionalInt(arguments, "page_size");
                if (ps.HasValue)
                {
                    if (ps.Value <= 0)
                        throw new ArgumentException("page_size must be positive");
                    pageSize = Math.Min(ps.Value, 1000);
                }
            }
            return (offset, pageSize);
        }

        /// <summary>
        /// True if any type in <paramref name="type"/>'s base chain has a FullName containing
        /// <paramref name="baseNeedle"/> (case-insensitive) — e.g. base_type "MonoBehaviour" matches
        /// all (transitive) UnityEngine.MonoBehaviour subclasses. Walks up via ResolveTypeDef, capped.
        /// </summary>
        static bool InheritsFrom(TypeDef type, string baseNeedle)
        {
            var cur = type.BaseType;
            int guard = 0;
            while (cur != null && guard++ < 50)
            {
                if (cur.FullName.IndexOf(baseNeedle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                var def = cur.ResolveTypeDef();
                if (def == null)
                    break;
                cur = def.BaseType;
            }
            return false;
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
        /// Returns (offset, pageSize) tuple. Returns (0, 10) if cursor is null/empty.
        /// Throws ArgumentException for invalid cursors (per MCP protocol, error code -32602).
        /// </summary>
        (int offset, int pageSize) DecodeCursor(string? cursor)
        {
            const int defaultPageSize = 10;

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
