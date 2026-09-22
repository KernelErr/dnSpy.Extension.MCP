using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace dnSpy.Extension.MCP.Headless {
	/// <summary>
	/// The MCP stdio transport: newline-delimited JSON-RPC, one message per line — requests on stdin,
	/// responses on stdout (UTF-8 compact JSON, so never an embedded newline). Requests are handled in
	/// order and notifications get no reply. Returns when stdin closes, the client's signal to shut down.
	/// </summary>
	sealed class StdioTransport {
		readonly McpDispatcher dispatcher;
		readonly McpSettings settings;

		public StdioTransport(McpDispatcher dispatcher, McpSettings settings) {
			this.dispatcher = dispatcher;
			this.settings = settings;
		}

		public int Run(Stream input, Stream output) {
			var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
			var reader = new StreamReader(input, utf8);
			var writer = new StreamWriter(output, utf8) { AutoFlush = true, NewLine = "\n" };

			string? line;
			while ((line = reader.ReadLine()) != null) {
				if (line.Trim().Length == 0)
					continue;

				McpRequest? request;
				try {
					request = JsonSerializer.Deserialize<McpRequest>(line);
				}
				catch (JsonException ex) {
					settings.Log($"Unparseable message: {ex.Message}");
					writer.WriteLine(ErrorWithNullId(-32700, $"Parse error: {ex.Message}"));
					continue;
				}
				// A message without a method is a response to a server-to-client request. This server
				// never sends one, so there is nothing to match it to.
				if (request == null || string.IsNullOrEmpty(request.Method))
					continue;

				var response = dispatcher.Handle(request);
				// Notifications (no id) get no reply.
				if (request.Id == null || request.Method.StartsWith("notifications/", StringComparison.Ordinal))
					continue;
				writer.WriteLine(JsonSerializer.Serialize(response, McpDispatcher.JsonOptions));
			}

			settings.Log("stdin closed; exiting");
			return 0;
		}

		// McpDispatcher.JsonOptions omits nulls, but JSON-RPC wants an explicit "id": null on an error
		// for a message that couldn't be parsed far enough to read its id.
		static string ErrorWithNullId(int code, string message) =>
			JsonSerializer.Serialize(new { jsonrpc = "2.0", id = (object?)null, error = new { code, message } });
	}
}
