using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// Transport-independent MCP request handling: JSON-RPC method dispatch, the initialize handshake,
	/// tools and resources. <see cref="McpServer"/> feeds it requests that arrive over HTTP inside
	/// dnSpy; the headless host (dnSpy.Extension.MCP.Headless) feeds it requests that arrive over stdio.
	/// </summary>
	sealed class McpDispatcher {
		/// <summary>
		/// Serializer options for JSON-RPC payloads: nulls are omitted, as JSON-RPC 2.0 / MCP require.
		/// </summary>
		public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		// MCP protocol versions this server can speak (newest first). The transports support both
		// the 2024-11-05 HTTP+SSE flow and the 2025-03-26+ Streamable HTTP flow (plus stdio, which
		// every revision shares), and the tools/resources are version-agnostic. On initialize we echo
		// the client's requested version when it's one of these (per the MCP lifecycle spec),
		// otherwise we fall back to our newest supported version.
		static readonly string[] supportedProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

		/// <summary>
		/// Reported as serverInfo.version: the extension's own release version, not dnSpy's. The csproj
		/// stamps McpExtensionVersion into AssemblyInformationalVersion and release.yml sets it from the
		/// release tag, so a client (or a user debugging a stale deploy) can tell builds apart.
		/// </summary>
		public static readonly string ServerVersion =
			typeof(McpDispatcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? "0.0.0-dev";

		readonly McpTools tools;
		readonly BepInExResources bepinexResources;
		readonly McpSettings settings;

		public McpDispatcher(McpTools tools, BepInExResources bepinexResources, McpSettings settings) {
			this.tools = tools;
			this.bepinexResources = bepinexResources;
			this.settings = settings;
		}

		/// <summary>
		/// Handles one JSON-RPC message. For a notification the returned response carries an empty
		/// result that the transport must not send back.
		/// </summary>
		public McpResponse Handle(McpRequest request) {
			try {
				// Handle notifications (no response needed)
				if (request.Method.StartsWith("notifications/")) {
					// Notifications don't require a response, but we log them
					settings.Log($"MCP notification: {request.Method}");
					return new McpResponse {
						JsonRpc = "2.0",
						Id = request.Id,
						Result = new { }
					};
				}

				settings.Log($"MCP request: {request.Method}");

				var result = request.Method switch {
					"initialize" => HandleInitialize(request.Params),
					"ping" => HandlePing(),
					"tools/list" => HandleListTools(),
					"tools/call" => HandleCallTool(request.Params),
					"resources/list" => HandleListResources(),
					"resources/templates/list" => HandleListResourceTemplates(),
					"resources/read" => HandleReadResource(request.Params),
					_ => throw new MethodNotFoundException(request.Method)
				};

				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Result = result
				};
			}
			catch (MethodNotFoundException ex) {
				// JSON-RPC 2.0 reserves -32601 for this. Not logged as an ERROR: it is a client probing
				// for an optional method we don't implement, not a failure on our side.
				settings.Log($"Unsupported method: {request.Method}");
				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Error = new McpError {
						Code = -32601,
						Message = ex.Message
					}
				};
			}
			catch (ArgumentException ex) {
				// ArgumentException indicates invalid parameters (MCP error code -32602)
				settings.Log($"Invalid params in {request.Method}: {ex.Message}");
				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Error = new McpError {
						Code = -32602,
						Message = ex.Message
					}
				};
			}
			catch (Exception ex) {
				// Other exceptions are internal errors (MCP error code -32603)
				settings.Log($"ERROR in {request.Method}: {ex.Message}");
				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Error = new McpError {
						Code = -32603,
						Message = ex.Message
					}
				};
			}
		}

		object HandleInitialize(Dictionary<string, object>? parameters) {
			// Per the MCP lifecycle spec the server MUST reply with the client's requested
			// protocol version if it supports it, otherwise with its own newest supported
			// version. Hardcoding 2024-11-05 (the pre-Streamable-HTTP revision) ignored the
			// client's request; we negotiate instead.
			return new InitializeResult {
				ProtocolVersion = NegotiateProtocolVersion(parameters),
				Capabilities = new ServerCapabilities {
					Tools = new Dictionary<string, object>(),
					Resources = new Dictionary<string, object>()
				},
				ServerInfo = new ServerInfo {
					Name = "dnSpy MCP Server",
					Version = ServerVersion
				}
			};
		}

		/// <summary>
		/// Picks the protocol version to advertise in the initialize result: the client's
		/// requested version when we support it, else our newest supported version. The
		/// client sends <c>protocolVersion</c> in the initialize params; with
		/// <see cref="System.Text.Json"/> the value arrives as a <see cref="JsonElement"/>.
		/// </summary>
		static string NegotiateProtocolVersion(Dictionary<string, object>? parameters) {
			string? requested = null;
			if (parameters != null && parameters.TryGetValue("protocolVersion", out var pv)) {
				if (pv is JsonElement je && je.ValueKind == JsonValueKind.String)
					requested = je.GetString();
				else if (pv is string s)
					requested = s;
			}

			if (!string.IsNullOrEmpty(requested) && Array.IndexOf(supportedProtocolVersions, requested) >= 0)
				return requested!;

			return supportedProtocolVersions[0];
		}

		object HandlePing() {
			// Simple ping/pong for keepalive
			return new { };
		}

		object HandleListTools() {
			return new ListToolsResult {
				Tools = tools.GetAvailableTools()
			};
		}

		object HandleCallTool(Dictionary<string, object>? parameters) {
			if (parameters == null)
				throw new ArgumentException("Parameters required");

			var toolCallJson = JsonSerializer.Serialize(parameters);
			var toolCall = JsonSerializer.Deserialize<CallToolRequest>(toolCallJson);

			if (toolCall == null)
				throw new ArgumentException("Invalid tool call parameters");

			return tools.ExecuteTool(toolCall.Name, toolCall.Arguments);
		}

		object HandleListResources() {
			return new ListResourcesResult {
				Resources = bepinexResources.GetResources()
			};
		}

		// MCP clients (Claude, MCP Inspector) probe this during initialization to discover
		// parameterized resource URI templates. We expose none, so answer with an empty list rather
		// than failing the probe (which also logged an error on every connect).
		object HandleListResourceTemplates() => new { resourceTemplates = Array.Empty<object>() };

		object HandleReadResource(Dictionary<string, object>? parameters) {
			if (parameters == null)
				throw new ArgumentException("Parameters required");

			var requestJson = JsonSerializer.Serialize(parameters);
			var readRequest = JsonSerializer.Deserialize<ReadResourceRequest>(requestJson);

			if (readRequest == null || string.IsNullOrEmpty(readRequest.Uri))
				throw new ArgumentException("Resource URI required");

			var content = bepinexResources.ReadResource(readRequest.Uri);
			if (content == null)
				throw new ArgumentException($"Resource not found: {readRequest.Uri}");

			return new ReadResourceResult {
				Contents = new List<ResourceContent> {
					new ResourceContent {
						Uri = readRequest.Uri,
						MimeType = "text/markdown",
						Text = content
					}
				}
			};
		}
	}

	/// <summary>
	/// Thrown by <see cref="McpDispatcher"/> for a JSON-RPC method it doesn't implement, so the
	/// client gets -32601 (Method not found) rather than the -32603 internal-error code.
	/// </summary>
	sealed class MethodNotFoundException : Exception {
		public MethodNotFoundException(string method) : base($"Method not found: {method}") { }
	}
}
