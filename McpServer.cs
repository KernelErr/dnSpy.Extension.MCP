using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
#if NET
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
#endif

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// HTTP server implementing the Model Context Protocol (MCP) for exposing dnSpy analysis tools to AI assistants.
	/// Uses Kestrel for .NET 8.0+ and HttpListener for .NET Framework 4.8.
	/// </summary>
	[Export(typeof(McpServer))]
	sealed class McpServer : IDisposable {
		readonly McpSettings settings;
		readonly McpTools tools;
		readonly BepInExResources bepinexResources;
#if NET
		IHost? webHost;
#else
		HttpListener? httpListener;
#endif
		CancellationTokenSource? cts;

		// JSON serialization options to ignore null values (JSON-RPC 2.0 requirement)
		static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions {
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

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
			if (!settings.EnableServer)
				return;

#if NET
			if (webHost != null)
				return; // Already running
			settings.Log($"Starting MCP server on port {settings.Port}");
#else
			if (httpListener != null)
				return; // Already running
			settings.Log($"Starting MCP server on {settings.Host}:{settings.Port}");
#endif

			try {
				cts = new CancellationTokenSource();

#if NET
				StartKestrelServer();
#else
				StartHttpListenerServer();
#endif
			}
			catch (Exception ex) {
				settings.Log($"ERROR starting server: {ex.GetType().Name}: {ex.Message}");
			}
		}

#if NET
		void StartKestrelServer() {
			Task.Run(() => {
				try {
					var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
						WebRootPath = null
					});

					// Configure Kestrel to listen on the specified port
					builder.WebHost.ConfigureKestrel(options => {
						options.ListenAnyIP(settings.Port);
					});

					var app = builder.Build();

					// Enable CORS for all origins
					app.Use(async (context, next) => {
						context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
						context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
						context.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type");

						if (context.Request.Method == "OPTIONS") {
							context.Response.StatusCode = 200;
							return;
						}

						await next();
					});

					// MCP endpoint
					app.MapPost("/", async context => {
						try {
							using var reader = new StreamReader(context.Request.Body);
							var body = await reader.ReadToEndAsync();

							var request = JsonSerializer.Deserialize<McpRequest>(body);
							if (request == null) {
								context.Response.StatusCode = 400;
								await context.Response.WriteAsync("Invalid request");
								return;
							}

							var response = HandleRequest(request);
							var responseJson = JsonSerializer.Serialize(response, jsonOptions);

							context.Response.ContentType = "application/json";
							await context.Response.WriteAsync(responseJson);
						}
						catch (Exception ex) {
							settings.Log($"ERROR handling request: {ex.Message}");
							var errorResponse = new McpResponse {
								JsonRpc = "2.0",
								Error = new McpError {
									Code = -32603,
									Message = "Internal error",
									Data = ex.Message
								}
							};

							context.Response.ContentType = "application/json";
							await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse, jsonOptions));
						}
					});

					// Health check endpoint
					app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "dnSpy MCP Server" }));

					webHost = app;
					settings.Log($"MCP server started on port {settings.Port}");
					app.Run();
				}
				catch (Exception ex) {
					settings.Log($"ERROR starting Kestrel server: {ex.GetType().Name}: {ex.Message}");
					webHost = null;
				}
			}, cts!.Token);
		}
#else
		void StartHttpListenerServer() {
			Task.Run(() => {
				try {
					httpListener = new HttpListener();
					var prefix = $"http://{settings.Host}:{settings.Port}/";
					httpListener.Prefixes.Add(prefix);
					httpListener.Start();
					settings.Log($"MCP server started on {settings.Host}:{settings.Port}");

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

		void HandleHttpRequest(HttpListenerContext context) {
			try {
				// Enable CORS
				context.Response.AddHeader("Access-Control-Allow-Origin", "*");
				context.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
				context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

				if (context.Request.HttpMethod == "OPTIONS") {
					context.Response.StatusCode = 200;
					context.Response.Close();
					return;
				}

				if (context.Request.Url?.AbsolutePath == "/health" && context.Request.HttpMethod == "GET") {
					var healthResponse = "{\"status\":\"ok\",\"service\":\"dnSpy MCP Server\"}";
					var buffer = Encoding.UTF8.GetBytes(healthResponse);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = buffer.Length;
					context.Response.OutputStream.Write(buffer, 0, buffer.Length);
					context.Response.Close();
					return;
				}

				if (context.Request.Url?.AbsolutePath == "/" && context.Request.HttpMethod == "POST") {
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
				else {
					context.Response.StatusCode = 404;
					context.Response.Close();
				}
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
#endif

		/// <summary>
		/// Stops the MCP server if it's running.
		/// </summary>
		public void Stop() {
			try {
				cts?.Cancel();
#if NET
				webHost?.StopAsync().Wait(TimeSpan.FromSeconds(5));
				webHost?.Dispose();
				webHost = null;
				settings.Log("MCP server stopped");
#else
				httpListener?.Stop();
				httpListener?.Close();
				httpListener = null;
				settings.Log("MCP server stopped");
#endif
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
			catch (Exception ex) {
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
}
