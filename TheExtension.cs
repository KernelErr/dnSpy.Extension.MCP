using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Extension;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// Main extension entry point for the MCP (Model Context Protocol) Server.
	/// This extension enables AI assistants to analyze .NET assemblies loaded in dnSpy.
	/// </summary>
	[ExportExtension]
	sealed class TheExtension : IExtension {
		readonly McpServer mcpServer;
		readonly McpSettings settings;

		/// <summary>
		/// Initializes the MCP extension and links the settings with the server.
		/// </summary>
		[ImportingConstructor]
		public TheExtension(McpServer mcpServer, McpSettings settings) {
			this.mcpServer = mcpServer;
			this.settings = settings;

			// Allow settings to control the server dynamically
			if (settings is McpSettingsImpl settingsImpl)
				settingsImpl.SetServer(mcpServer);
		}

		/// <summary>
		/// Gets merged resource dictionaries. This extension does not provide any.
		/// </summary>
		public IEnumerable<string> MergedResourceDictionaries {
			get {
				yield break;
			}
		}

		/// <summary>
		/// Gets information about this extension.
		/// </summary>
		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "MCP Server for AI-assisted .NET assembly analysis and BepInEx plugin development",
		};

		/// <summary>
		/// Handles extension lifecycle events.
		/// </summary>
		/// <param name="event">The extension event type.</param>
		/// <param name="obj">Event-specific data.</param>
		public void OnEvent(ExtensionEvent @event, object? obj) {
			if (@event == ExtensionEvent.Loaded) {
				settings.Log("MCP Extension loaded");
				mcpServer.Start();
			}
			else if (@event == ExtensionEvent.AppExit) {
				settings.Log("MCP Extension unloading");
				mcpServer.Stop();
			}
		}
	}
}
