using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// HTTP server implementing the Model Context Protocol (MCP) for exposing dnSpy analysis tools to AI assistants.
	/// Uses <see cref="HttpListener"/> on both .NET Framework 4.8 and .NET 10. Kestrel was considered
	/// but dropped: dnSpy's self-contained .NET bundle does not include ASP.NET Core, so MEF
	/// composition of this type would fail with a silent TypeLoadException if Kestrel types were
	/// referenced here.
	/// </summary>
	[Export(typeof(McpServer))]
	sealed class McpServer : IDisposable {
		readonly McpSettings settings;
		readonly McpTools tools;
		readonly BepInExResources bepinexResources;
		HttpListener? httpListener;
		CancellationTokenSource? cts;
		int actualPort;
		readonly ConcurrentDictionary<string, SseSession> sseSessions = new ConcurrentDictionary<string, SseSession>();
		readonly ConcurrentDictionary<string, StreamableHttpSession> streamableSessions = new ConcurrentDictionary<string, StreamableHttpSession>();

		/// <summary>
		/// The port the server is actually listening on. May differ from <see cref="McpSettings.Port"/>
		/// if that port was taken and fallback to port+1 was used.
		/// </summary>
		public int ActualPort => actualPort;

		// JSON serialization options to ignore null values (JSON-RPC 2.0 requirement)
		static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions {
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		const int portSearchAttempts = 20;

		/// <summary>
		/// Probes for an available TCP port on any interface, starting at <paramref name="startPort"/>
		/// and incrementing up to <paramref name="maxAttempts"/> times. Returns the first port that
		/// can be bound. There is a TOCTOU race here (another process could steal the port before
		/// the real server binds it), but it is good enough for a local dev tool.
		/// </summary>
		static int FindAvailablePort(int startPort, int maxAttempts) {
			for (int i = 0; i < maxAttempts; i++) {
				int port = startPort + i;
				if (port < 1 || port > 65535)
					break;
				TcpListener? listener = null;
				try {
					listener = new TcpListener(IPAddress.Any, port);
					listener.Start();
					return port;
				}
				catch (SocketException) {
					continue;
				}
				finally {
					listener?.Stop();
				}
			}
			throw new InvalidOperationException($"No available port in range {startPort}..{startPort + maxAttempts - 1}");
		}

		/// <summary>
		/// Initializes the MCP server with the specified settings, tools, and documentation.
		/// </summary>
		[ImportingConstructor]
		public McpServer(McpSettings settings, McpTools tools, BepInExResources bepinexResources) {
			this.settings = settings;
			this.tools = tools;
			this.bepinexResources = bepinexResources;
		}

		/// <summary>
		/// Starts the MCP server if enabled in settings.
		/// </summary>
		public void Start() {
			if (!settings.EnableServer) {
				settings.Log("Start() called but EnableServer is false; nothing to do.");
				return;
			}

			if (httpListener != null) {
				settings.Log("Start() called but httpListener is already running; ignoring.");
				return;
			}

			try {
				actualPort = FindAvailablePort(settings.Port, portSearchAttempts);
				if (actualPort != settings.Port)
					settings.Log($"Port {settings.Port} is in use; falling back to {actualPort}");

				settings.Log($"Starting MCP server on {settings.Host}:{actualPort}");
				cts = new CancellationTokenSource();
				StartHttpListenerServer();
			}
			catch (Exception ex) {
				settings.Log($"ERROR starting server: {ex.GetType().Name}: {ex.Message}");
			}
		}

		void StartHttpListenerServer() {
			Task.Run(() => {
				try {
					httpListener = StartBoundListener(actualPort);
					if (httpListener == null) {
						settings.Log("ERROR: HttpListener could not bind any loopback prefix.");
						return;
					}

					while (!cts!.Token.IsCancellationRequested) {
						try {
							var context = httpListener.GetContext();
							Task.Run(() => HandleHttpRequest(context), cts.Token);
						}
						catch (HttpListenerException) {
							break; // Listener was stopped
						}
						catch (Exception ex) {
							settings.Log($"ERROR accepting request: {ex.GetType().Name}: {ex.Message}");
						}
					}
				}
				catch (Exception ex) {
					settings.Log($"ERROR starting HttpListener: {ex.GetType().Name}: {ex.Message}");
					httpListener = null;
				}
			}, cts!.Token);
		}

		/// <summary>
		/// Creates and Start()s an <see cref="HttpListener"/> bound to loopback. We register BOTH the
		/// <c>localhost</c> hostname and the literal loopback IPs (<c>127.0.0.1</c>, <c>[::1]</c>) rather
		/// than only the configured host. http.sys matches a request's Host header against the exact
		/// registered prefix string, so a <c>localhost</c>-only prefix makes it reject a request to
		/// <c>http://127.0.0.1:port/</c> at the kernel level with "HTTP 400 - Invalid Hostname" before
		/// our code ever runs. All three loopback prefixes bind without admin; only the <c>+</c>/<c>*</c>
		/// wildcards need elevation. Falls back to fewer prefixes if a bind fails (e.g. IPv6 disabled).
		/// </summary>
		HttpListener? StartBoundListener(int port) {
			foreach (var prefixes in BuildLoopbackPrefixSets(settings.Host, port)) {
				var listener = new HttpListener();
				foreach (var p in prefixes)
					listener.Prefixes.Add(p);
				try {
					listener.Start();
					settings.Log($"MCP server started, listening on: {string.Join(", ", prefixes)}");
					return listener;
				}
				catch (Exception ex) {
					settings.Log($"Could not bind [{string.Join(", ", prefixes)}]: {ex.GetType().Name}: {ex.Message}");
					try { listener.Close(); } catch { /* ignore */ }
				}
			}
			return null;
		}

		/// <summary>
		/// Candidate prefix sets to try, most-complete first. For a loopback host we want
		/// <c>localhost</c> AND both loopback IP literals so clients can reach the server by name or by
		/// IP. A non-loopback host (an explicit IP, or <c>+</c>/<c>*</c> for LAN access) is honored
		/// verbatim and may require admin.
		/// </summary>
		static IEnumerable<List<string>> BuildLoopbackPrefixSets(string host, int port) {
			bool isLoopback =
				string.IsNullOrEmpty(host) ||
				host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
				host == "127.0.0.1" ||
				host == "::1" || host == "[::1]";

			if (!isLoopback) {
				yield return new List<string> { $"http://{host}:{port}/" };
				yield break;
			}

			yield return new List<string> {
				$"http://localhost:{port}/",
				$"http://127.0.0.1:{port}/",
				$"http://[::1]:{port}/",
			};
			// IPv6 loopback may not be bindable (IPv6 disabled on the box) — drop it.
			yield return new List<string> {
				$"http://localhost:{port}/",
				$"http://127.0.0.1:{port}/",
			};
			// Last resort: the original localhost-only behavior.
			yield return new List<string> { $"http://localhost:{port}/" };
		}

		void HandleHttpRequest(HttpListenerContext context) {
			try {
				// Enable CORS. `Mcp-Session-Id` must be both accepted on requests and exposed on
				// responses so Streamable HTTP clients (codex, MCP Inspector, ...) can read the
				// session ID that the server allocates on `initialize`.
				context.Response.AddHeader("Access-Control-Allow-Origin", "*");
				context.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
				context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept, Mcp-Session-Id, MCP-Protocol-Version");
				context.Response.AddHeader("Access-Control-Expose-Headers", "Mcp-Session-Id");

				if (context.Request.HttpMethod == "OPTIONS") {
					context.Response.StatusCode = 200;
					context.Response.Close();
					return;
				}

				var path = context.Request.Url?.AbsolutePath ?? string.Empty;
				var httpMethod = context.Request.HttpMethod;

				if (path == "/health" && httpMethod == "GET") {
					var healthResponse = "{\"status\":\"ok\",\"service\":\"dnSpy MCP Server\"}";
					var buffer = Encoding.UTF8.GetBytes(healthResponse);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = buffer.Length;
					context.Response.OutputStream.Write(buffer, 0, buffer.Length);
					context.Response.Close();
					return;
				}

				if (path == "/sse" && httpMethod == "GET") {
					HandleLegacySseGet(context);
					return;
				}

				if (path == "/message" && httpMethod == "POST") {
					HandleLegacySsePost(context);
					return;
				}

				// MCP Streamable HTTP (spec revision 2025-03-26) uses a single endpoint for
				// POST / GET / DELETE. We accept both "/" and "/mcp" so that codex-style
				// `type = "streamable-http"` configs pointing at http://host:port work without
				// a path suffix, while still matching clients that hit /mcp explicitly.
				bool isStreamablePath = path == "/" || path == "/mcp";
				if (isStreamablePath) {
					var accept = context.Request.Headers["Accept"] ?? string.Empty;
					bool acceptsEventStream = accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;

					if (httpMethod == "POST") {
						// Streamable HTTP clients always include text/event-stream in Accept;
						// plain-JSON clients (curl, legacy docs) do not. Disambiguate on that.
						if (acceptsEventStream)
							HandleStreamableHttpPost(context);
						else
							HandleLegacyPlainPost(context);
						return;
					}

					if (httpMethod == "GET" && acceptsEventStream) {
						HandleStreamableHttpGet(context);
						return;
					}

					if (httpMethod == "GET") {
						// A plain browser/curl GET (no event-stream Accept) is not an MCP client.
						// Serve a human-readable status page instead of a 404 so opening it in a
						// browser to check the server is alive actually works.
						HandleStatusPage(context);
						return;
					}

					if (httpMethod == "DELETE") {
						HandleStreamableHttpDelete(context);
						return;
					}
				}

				context.Response.StatusCode = 404;
				context.Response.Close();
			}
			catch (Exception ex) {
				try {
					settings.Log($"ERROR in HandleHttpRequest: {ex.GetType().Name}: {ex.Message}");
					var errorResponse = new McpResponse {
						JsonRpc = "2.0",
						Error = new McpError {
							Code = -32603,
							Message = "Internal error",
							Data = ex.Message
						}
					};

					var responseJson = JsonSerializer.Serialize(errorResponse, jsonOptions);
					var responseBytes = Encoding.UTF8.GetBytes(responseJson);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = responseBytes.Length;
					context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
					context.Response.Close();
				}
				catch {
					// Failed to send error response
				}
			}
		}

		/// <summary>
		/// Legacy MCP 2024-11-05 SSE transport: GET /sse opens a long-lived event-stream.
		/// The handler holds the HttpListener response open until the client disconnects or
		/// the server shuts down. Responses to posted messages are written back over this
		/// same stream as `message` events.
		/// </summary>
		void HandleLegacySseGet(HttpListenerContext context) {
			context.Response.ContentType = "text/event-stream";
			context.Response.Headers["Cache-Control"] = "no-cache";
			context.Response.SendChunked = true;
			context.Response.KeepAlive = true;

			var sessionId = Guid.NewGuid().ToString("N");
			var session = new SseSession(sessionId, context.Response.OutputStream);
			sseSessions[sessionId] = session;
			settings.Log($"SSE session opened: {sessionId}");

			try {
				session.WriteEvent("endpoint", $"/message?sessionId={sessionId}");

				while (!cts!.Token.IsCancellationRequested) {
					Thread.Sleep(15000);
					try {
						session.WriteComment("ping");
					}
					catch {
						break;
					}
				}
			}
			finally {
				sseSessions.TryRemove(sessionId, out _);
				settings.Log($"SSE session closed: {sessionId}");
				try { context.Response.OutputStream.Close(); } catch { /* ignore */ }
				try { context.Response.Close(); } catch { /* ignore */ }
			}
		}

		void HandleLegacySsePost(HttpListenerContext context) {
			var sessionId = context.Request.QueryString["sessionId"];
			if (string.IsNullOrEmpty(sessionId) || !sseSessions.TryGetValue(sessionId!, out var session)) {
				context.Response.StatusCode = 404;
				var bytes = Encoding.UTF8.GetBytes("Unknown sessionId");
				context.Response.OutputStream.Write(bytes, 0, bytes.Length);
				context.Response.Close();
				return;
			}

			using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
			var body = reader.ReadToEnd();

			var request = JsonSerializer.Deserialize<McpRequest>(body);
			if (request == null) {
				context.Response.StatusCode = 400;
				context.Response.Close();
				return;
			}

			// Ack the POST. The JSON-RPC response is delivered over the SSE stream.
			context.Response.StatusCode = 202;
			var ack = Encoding.UTF8.GetBytes("Accepted");
			context.Response.OutputStream.Write(ack, 0, ack.Length);
			context.Response.Close();

			try {
				bool isNotification = request.Method?.StartsWith("notifications/", StringComparison.Ordinal) ?? false;
				var response = HandleRequest(request);
				if (!isNotification) {
					var responseJson = JsonSerializer.Serialize(response, jsonOptions);
					session.WriteEvent("message", responseJson);
				}
			}
			catch (Exception ex) {
				settings.Log($"ERROR writing SSE message: {ex.Message}");
			}
		}

		void HandleLegacyPlainPost(HttpListenerContext context) {
			using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
			var body = reader.ReadToEnd();

			var request = JsonSerializer.Deserialize<McpRequest>(body);
			if (request == null) {
				context.Response.StatusCode = 400;
				var errorBytes = Encoding.UTF8.GetBytes("Invalid request");
				context.Response.OutputStream.Write(errorBytes, 0, errorBytes.Length);
				context.Response.Close();
				return;
			}

			var response = HandleRequest(request);
			var responseJson = JsonSerializer.Serialize(response, jsonOptions);
			var responseBytes = Encoding.UTF8.GetBytes(responseJson);

			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = responseBytes.Length;
			context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
			context.Response.Close();
		}

		/// <summary>
		/// MCP Streamable HTTP (2025-03-26) POST handler. Parses the JSON-RPC body, allocates a
		/// session ID on `initialize` and echoes back the session ID on subsequent calls via the
		/// `Mcp-Session-Id` header. Responses are returned inline as `application/json` (allowed
		/// by the spec as an alternative to an SSE stream). Notifications get `202 Accepted`.
		/// </summary>
		void HandleStreamableHttpPost(HttpListenerContext context) {
			using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
			var body = reader.ReadToEnd();

			McpRequest? request;
			try {
				request = JsonSerializer.Deserialize<McpRequest>(body);
			}
			catch (JsonException ex) {
				settings.Log($"Streamable HTTP parse error: {ex.Message}");
				context.Response.StatusCode = 400;
				var bytes = Encoding.UTF8.GetBytes("Parse error");
				context.Response.OutputStream.Write(bytes, 0, bytes.Length);
				context.Response.Close();
				return;
			}

			if (request == null || string.IsNullOrEmpty(request.Method)) {
				context.Response.StatusCode = 400;
				var bytes = Encoding.UTF8.GetBytes("Invalid request");
				context.Response.OutputStream.Write(bytes, 0, bytes.Length);
				context.Response.Close();
				return;
			}

			var headerSessionId = context.Request.Headers["Mcp-Session-Id"];
			bool isInitialize = string.Equals(request.Method, "initialize", StringComparison.Ordinal);

			if (isInitialize) {
				var newId = Guid.NewGuid().ToString("N");
				streamableSessions[newId] = new StreamableHttpSession(newId);
				context.Response.Headers["Mcp-Session-Id"] = newId;
				settings.Log($"Streamable HTTP session opened: {newId}");
			}
			else if (!string.IsNullOrEmpty(headerSessionId) && !streamableSessions.ContainsKey(headerSessionId!)) {
				// If the client presents a session ID we don't recognise, reject — the client
				// should then re-initialize. Missing header is tolerated for leniency.
				context.Response.StatusCode = 404;
				var bytes = Encoding.UTF8.GetBytes("Unknown Mcp-Session-Id");
				context.Response.OutputStream.Write(bytes, 0, bytes.Length);
				context.Response.Close();
				return;
			}

			bool isNotification = request.Method.StartsWith("notifications/", StringComparison.Ordinal) || request.Id == null;

			if (isNotification) {
				HandleRequest(request);
				context.Response.StatusCode = 202;
				context.Response.ContentLength64 = 0;
				context.Response.Close();
				return;
			}

			var response = HandleRequest(request);
			var responseJson = JsonSerializer.Serialize(response, jsonOptions);
			var responseBytes = Encoding.UTF8.GetBytes(responseJson);
			context.Response.StatusCode = 200;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = responseBytes.Length;
			context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
			context.Response.Close();
		}

		/// <summary>
		/// MCP Streamable HTTP GET handler. Opens a long-lived SSE stream for server-initiated
		/// messages on an existing session. This server currently has no server-initiated
		/// requests, so the stream just emits keep-alive pings until the client disconnects.
		/// </summary>
		void HandleStreamableHttpGet(HttpListenerContext context) {
			var sessionId = context.Request.Headers["Mcp-Session-Id"];
			if (string.IsNullOrEmpty(sessionId) || !streamableSessions.ContainsKey(sessionId!)) {
				context.Response.StatusCode = 404;
				var bytes = Encoding.UTF8.GetBytes("Unknown Mcp-Session-Id");
				context.Response.OutputStream.Write(bytes, 0, bytes.Length);
				context.Response.Close();
				return;
			}

			context.Response.ContentType = "text/event-stream";
			context.Response.Headers["Cache-Control"] = "no-cache";
			context.Response.SendChunked = true;
			context.Response.KeepAlive = true;

			settings.Log($"Streamable HTTP GET stream opened: {sessionId}");
			var session = new SseSession(sessionId!, context.Response.OutputStream);
			try {
				while (!cts!.Token.IsCancellationRequested) {
					Thread.Sleep(15000);
					try {
						session.WriteComment("ping");
					}
					catch {
						break;
					}
				}
			}
			finally {
				settings.Log($"Streamable HTTP GET stream closed: {sessionId}");
				try { context.Response.OutputStream.Close(); } catch { /* ignore */ }
				try { context.Response.Close(); } catch { /* ignore */ }
			}
		}

		/// <summary>
		/// MCP Streamable HTTP DELETE handler. Terminates the session identified by
		/// `Mcp-Session-Id`. Returns 200 even when the session is unknown so that clients
		/// can idempotently tear down.
		/// </summary>
		void HandleStreamableHttpDelete(HttpListenerContext context) {
			var sessionId = context.Request.Headers["Mcp-Session-Id"];
			if (!string.IsNullOrEmpty(sessionId) && streamableSessions.TryRemove(sessionId!, out _))
				settings.Log($"Streamable HTTP session closed by DELETE: {sessionId}");
			context.Response.StatusCode = 200;
			context.Response.ContentLength64 = 0;
			context.Response.Close();
		}

		/// <summary>
		/// Serves a small human-readable status page for a plain browser GET on the root. The MCP
		/// endpoints only answer POST (JSON-RPC) and SSE, so a browser would otherwise get a bare
		/// 404 and look broken; this confirms the server is up and points at the real endpoints.
		/// </summary>
		void HandleStatusPage(HttpListenerContext context) {
			var html =
				"<!doctype html><html><head><meta charset=\"utf-8\"><title>dnSpy MCP Server</title></head>" +
				"<body style=\"font-family:system-ui,sans-serif;max-width:42rem;margin:3rem auto;line-height:1.5\">" +
				"<h1>dnSpy MCP Server</h1>" +
				$"<p><b>Status:</b> running on port {actualPort}.</p>" +
				"<p>This is a Model Context Protocol (JSON-RPC) endpoint, not a website — there is nothing " +
				"to browse here. Point an MCP client at it instead.</p>" +
				"<ul>" +
				"<li><code>GET /health</code> — liveness probe (<a href=\"/health\">/health</a>)</li>" +
				"<li><code>POST /</code> — JSON-RPC (plain HTTP or MCP Streamable HTTP)</li>" +
				"<li><code>GET /sse</code> — legacy MCP SSE transport</li>" +
				"</ul></body></html>";
			var buffer = Encoding.UTF8.GetBytes(html);
			context.Response.StatusCode = 200;
			context.Response.ContentType = "text/html; charset=utf-8";
			context.Response.ContentLength64 = buffer.Length;
			context.Response.OutputStream.Write(buffer, 0, buffer.Length);
			context.Response.Close();
		}

		/// <summary>
		/// Stops the MCP server if it's running.
		/// </summary>
		public void Stop() {
			try {
				cts?.Cancel();
				httpListener?.Stop();
				httpListener?.Close();
				httpListener = null;
				settings.Log("MCP server stopped");
				cts?.Dispose();
				cts = null;
			}
			catch (Exception ex) {
				settings.Log($"ERROR stopping server: {ex.GetType().Name}: {ex.Message}");
			}
		}

		McpResponse HandleRequest(McpRequest request) {
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
					"initialize" => HandleInitialize(),
					"ping" => HandlePing(),
					"tools/list" => HandleListTools(),
					"tools/call" => HandleCallTool(request.Params),
					"resources/list" => HandleListResources(),
					"resources/read" => HandleReadResource(request.Params),
					_ => throw new Exception($"Unknown method: {request.Method}")
				};

				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Result = result
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

		object HandleInitialize() {
			return new InitializeResult {
				ProtocolVersion = "2024-11-05",
				Capabilities = new ServerCapabilities {
					Tools = new Dictionary<string, object>(),
					Resources = new Dictionary<string, object>()
				},
				ServerInfo = new ServerInfo {
					Name = "dnSpy MCP Server",
					Version = "1.0.0"
				}
			};
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

		/// <summary>
		/// Disposes the server and releases all resources.
		/// </summary>
		public void Dispose() {
			Stop();
		}
	}

	/// <summary>
	/// A single MCP SSE transport session. Wraps the server-side response stream that the
	/// client is reading from, and serializes writes to it. The same stream is written to
	/// from the /sse handler (for the initial endpoint event and keep-alive pings) and from
	/// the /message POST handler (for JSON-RPC responses triggered by client requests), so
	/// all writes go through <see cref="writeLock"/>.
	/// </summary>
	sealed class SseSession {
		readonly Stream stream;
		readonly object writeLock = new object();

		public string Id { get; }

		public SseSession(string id, Stream stream) {
			Id = id;
			this.stream = stream;
		}

		/// <summary>
		/// Writes an SSE named event. <paramref name="data"/> is split on newlines so that
		/// multi-line JSON is encoded as multiple "data:" lines per the SSE spec.
		/// </summary>
		public void WriteEvent(string eventName, string data) {
			var sb = new StringBuilder();
			sb.Append("event: ").Append(eventName).Append('\n');
			foreach (var line in data.Split('\n'))
				sb.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
			sb.Append('\n');
			WriteRaw(sb.ToString());
		}

		/// <summary>
		/// Writes an SSE comment line. Used for keep-alive pings.
		/// </summary>
		public void WriteComment(string text) => WriteRaw(": " + text + "\n\n");

		void WriteRaw(string text) {
			var bytes = Encoding.UTF8.GetBytes(text);
			lock (writeLock) {
				stream.Write(bytes, 0, bytes.Length);
				stream.Flush();
			}
		}
	}

	/// <summary>
	/// A Streamable HTTP (MCP 2025-03-26) session. Unlike legacy SSE, the transport is
	/// POST-driven: each client POST returns its own JSON-RPC response inline, so the
	/// session only tracks identity and liveness rather than owning a response stream.
	/// </summary>
	sealed class StreamableHttpSession {
		public string Id { get; }
		public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

		public StreamableHttpSession(string id) {
			Id = id;
		}
	}
}
