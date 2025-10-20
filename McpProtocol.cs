using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace dnSpy.Extension.MCP {
	// MCP Protocol Models based on Model Context Protocol specification
	// Follows JSON-RPC 2.0 protocol with MCP-specific extensions

	/// <summary>
	/// Represents an MCP JSON-RPC 2.0 request.
	/// </summary>
	public class McpRequest {
		/// <summary>
		/// JSON-RPC version (always "2.0").
		/// </summary>
		[JsonPropertyName("jsonrpc")]
		public string JsonRpc { get; set; } = "2.0";

		/// <summary>
		/// Request identifier for matching responses.
		/// </summary>
		[JsonPropertyName("id")]
		public object? Id { get; set; }

		/// <summary>
		/// The method name to invoke.
		/// </summary>
		[JsonPropertyName("method")]
		public string Method { get; set; } = string.Empty;

		/// <summary>
		/// Optional method parameters.
		/// </summary>
		[JsonPropertyName("params")]
		public Dictionary<string, object>? Params { get; set; }
	}

	/// <summary>
	/// Represents an MCP JSON-RPC 2.0 response.
	/// </summary>
	public class McpResponse {
		/// <summary>
		/// JSON-RPC version (always "2.0").
		/// </summary>
		[JsonPropertyName("jsonrpc")]
		public string JsonRpc { get; set; } = "2.0";

		/// <summary>
		/// Request identifier matching the request.
		/// </summary>
		[JsonPropertyName("id")]
		public object? Id { get; set; }

		/// <summary>
		/// Result object (present on success).
		/// </summary>
		[JsonPropertyName("result")]
		public object? Result { get; set; }

		/// <summary>
		/// Error object (present on failure).
		/// </summary>
		[JsonPropertyName("error")]
		public McpError? Error { get; set; }
	}

	/// <summary>
	/// Represents a JSON-RPC 2.0 error.
	/// </summary>
	public class McpError {
		/// <summary>
		/// Error code (standard JSON-RPC codes).
		/// </summary>
		[JsonPropertyName("code")]
		public int Code { get; set; }

		/// <summary>
		/// Error message.
		/// </summary>
		[JsonPropertyName("message")]
		public string Message { get; set; } = string.Empty;

		/// <summary>
		/// Optional additional error data.
		/// </summary>
		[JsonPropertyName("data")]
		public object? Data { get; set; }
	}

	/// <summary>
	/// Describes an MCP tool with its schema.
	/// </summary>
	public class ToolInfo {
		/// <summary>
		/// Unique tool name.
		/// </summary>
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		/// <summary>
		/// Human-readable tool description.
		/// </summary>
		[JsonPropertyName("description")]
		public string Description { get; set; } = string.Empty;

		/// <summary>
		/// JSON Schema describing tool input parameters.
		/// </summary>
		[JsonPropertyName("inputSchema")]
		public Dictionary<string, object> InputSchema { get; set; } = new Dictionary<string, object>();
	}

	/// <summary>
	/// Result of the tools/list method.
	/// </summary>
	public class ListToolsResult {
		/// <summary>
		/// List of available tools.
		/// </summary>
		[JsonPropertyName("tools")]
		public List<ToolInfo> Tools { get; set; } = new List<ToolInfo>();
	}

	/// <summary>
	/// Request to call a specific tool.
	/// </summary>
	public class CallToolRequest {
		/// <summary>
		/// Name of the tool to call.
		/// </summary>
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		/// <summary>
		/// Tool-specific arguments.
		/// </summary>
		[JsonPropertyName("arguments")]
		public Dictionary<string, object>? Arguments { get; set; }
	}

	/// <summary>
	/// Result of a tool call.
	/// </summary>
	public class CallToolResult {
		/// <summary>
		/// Content returned by the tool.
		/// </summary>
		[JsonPropertyName("content")]
		public List<ToolContent> Content { get; set; } = new List<ToolContent>();

		/// <summary>
		/// Whether the tool execution resulted in an error.
		/// </summary>
		[JsonPropertyName("isError")]
		public bool IsError { get; set; }
	}

	/// <summary>
	/// Content item in a tool result.
	/// </summary>
	public class ToolContent {
		/// <summary>
		/// Content type (e.g., "text").
		/// </summary>
		[JsonPropertyName("type")]
		public string Type { get; set; } = "text";

		/// <summary>
		/// Text content.
		/// </summary>
		[JsonPropertyName("text")]
		public string Text { get; set; } = string.Empty;
	}

	/// <summary>
	/// Result of the initialize method.
	/// </summary>
	public class InitializeResult {
		/// <summary>
		/// MCP protocol version.
		/// </summary>
		[JsonPropertyName("protocolVersion")]
		public string ProtocolVersion { get; set; } = "2024-11-05";

		/// <summary>
		/// Server capabilities.
		/// </summary>
		[JsonPropertyName("capabilities")]
		public ServerCapabilities Capabilities { get; set; } = new ServerCapabilities();

		/// <summary>
		/// Server information.
		/// </summary>
		[JsonPropertyName("serverInfo")]
		public ServerInfo ServerInfo { get; set; } = new ServerInfo();
	}

	/// <summary>
	/// Describes server capabilities.
	/// </summary>
	public class ServerCapabilities {
		/// <summary>
		/// Tools capability configuration.
		/// </summary>
		[JsonPropertyName("tools")]
		public Dictionary<string, object>? Tools { get; set; } = new Dictionary<string, object>();

		/// <summary>
		/// Resources capability configuration.
		/// </summary>
		[JsonPropertyName("resources")]
		public Dictionary<string, object>? Resources { get; set; } = new Dictionary<string, object>();
	}

	/// <summary>
	/// Server identification information.
	/// </summary>
	public class ServerInfo {
		/// <summary>
		/// Server name.
		/// </summary>
		[JsonPropertyName("name")]
		public string Name { get; set; } = "dnSpy MCP Server";

		/// <summary>
		/// Server version.
		/// </summary>
		[JsonPropertyName("version")]
		public string Version { get; set; } = "1.0.0";
	}

	/// <summary>
	/// Represents a resource that can be read by MCP clients.
	/// </summary>
	public class ResourceInfo {
		/// <summary>
		/// Resource URI (unique identifier).
		/// </summary>
		[JsonPropertyName("uri")]
		public string Uri { get; set; } = string.Empty;

		/// <summary>
		/// Human-readable resource name.
		/// </summary>
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		/// <summary>
		/// Optional resource description.
		/// </summary>
		[JsonPropertyName("description")]
		public string? Description { get; set; }

		/// <summary>
		/// MIME type of the resource content.
		/// </summary>
		[JsonPropertyName("mimeType")]
		public string? MimeType { get; set; }
	}

	/// <summary>
	/// Result of the resources/list method.
	/// </summary>
	public class ListResourcesResult {
		/// <summary>
		/// List of available resources.
		/// </summary>
		[JsonPropertyName("resources")]
		public List<ResourceInfo> Resources { get; set; } = new List<ResourceInfo>();
	}

	/// <summary>
	/// Request to read a specific resource.
	/// </summary>
	public class ReadResourceRequest {
		/// <summary>
		/// URI of the resource to read.
		/// </summary>
		[JsonPropertyName("uri")]
		public string Uri { get; set; } = string.Empty;
	}

	/// <summary>
	/// Content of a resource.
	/// </summary>
	public class ResourceContent {
		/// <summary>
		/// Resource URI.
		/// </summary>
		[JsonPropertyName("uri")]
		public string Uri { get; set; } = string.Empty;

		/// <summary>
		/// MIME type of the content.
		/// </summary>
		[JsonPropertyName("mimeType")]
		public string? MimeType { get; set; }

		/// <summary>
		/// Text content (for text-based resources).
		/// </summary>
		[JsonPropertyName("text")]
		public string? Text { get; set; }

		/// <summary>
		/// Binary content as base64 (for binary resources).
		/// </summary>
		[JsonPropertyName("blob")]
		public string? Blob { get; set; }
	}

	/// <summary>
	/// Result of the resources/read method.
	/// </summary>
	public class ReadResourceResult {
		/// <summary>
		/// Resource contents.
		/// </summary>
		[JsonPropertyName("contents")]
		public List<ResourceContent> Contents { get; set; } = new List<ResourceContent>();
	}
}
