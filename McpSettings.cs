using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// Settings for the MCP server extension, including server configuration and logging.
	/// </summary>
	public class McpSettings : ViewModelBase {
		/// <summary>
		/// Gets or sets whether the MCP server is enabled.
		/// </summary>
		public bool EnableServer {
			get => enableServer;
			set {
				if (enableServer != value) {
					enableServer = value;
					OnPropertyChanged(nameof(EnableServer));
				}
			}
		}
		bool enableServer = false;

		/// <summary>
		/// Gets or sets the server host (default: localhost).
		/// </summary>
		public string Host {
			get => host;
			set {
				if (host != value) {
					host = value;
					OnPropertyChanged(nameof(Host));
				}
			}
		}
		string host = "localhost";

		/// <summary>
		/// Gets or sets the server port (default: 3000).
		/// </summary>
		public int Port {
			get => port;
			set {
				if (port != value) {
					port = value;
					OnPropertyChanged(nameof(Port));
				}
			}
		}
		int port = 3000;

		/// <summary>
		/// Gets the collection of log messages (limited to last 100 messages).
		/// </summary>
		public ObservableCollection<string> LogMessages { get; } = new ObservableCollection<string>();

		/// <summary>
		/// Gets or sets the combined log text for easy copying.
		/// </summary>
		string logText = string.Empty;
		public string LogText {
			get => logText;
			set {
				if (logText != value) {
					logText = value;
					OnPropertyChanged(nameof(LogText));
				}
			}
		}

		/// <summary>
		/// Adds a log message with timestamp to the log collection.
		/// </summary>
		/// <param name="message">The log message to add.</param>
		public void Log(string message) {
			var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
			var logEntry = $"[{timestamp}] {message}";

			// Add to collection on UI thread if available
			if (System.Windows.Application.Current?.Dispatcher != null) {
				System.Windows.Application.Current.Dispatcher.Invoke(() => {
					LogMessages.Add(logEntry);
					while (LogMessages.Count > 100)
						LogMessages.RemoveAt(0);
					LogText = string.Join(Environment.NewLine, LogMessages);
				});
			} else {
				LogMessages.Add(logEntry);
				while (LogMessages.Count > 100)
					LogMessages.RemoveAt(0);
				LogText = string.Join(Environment.NewLine, LogMessages);
			}
		}

		/// <summary>
		/// Creates a copy of these settings.
		/// </summary>
		public McpSettings Clone() => CopyTo(new McpSettings());

		/// <summary>
		/// Copies these settings to another instance.
		/// </summary>
		public McpSettings CopyTo(McpSettings other) {
			other.EnableServer = EnableServer;
			other.Host = Host;
			other.Port = Port;
			return other;
		}
	}

	/// <summary>
	/// Implementation of MCP settings with persistence support.
	/// </summary>
	[Export(typeof(McpSettings))]
	sealed class McpSettingsImpl : McpSettings {
		static readonly Guid SETTINGS_GUID = new Guid("352907A0-9DF5-4B2B-B47B-95E504CAC301");

		readonly ISettingsService settingsService;
		McpServer? mcpServer;

		[ImportingConstructor]
		McpSettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			// Load settings from persistent storage
			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			EnableServer = sect.Attribute<bool?>(nameof(EnableServer)) ?? EnableServer;
			Host = sect.Attribute<string>(nameof(Host)) ?? Host;
			Port = sect.Attribute<int?>(nameof(Port)) ?? Port;

			PropertyChanged += McpSettingsImpl_PropertyChanged;
		}

		/// <summary>
		/// Sets the server instance for dynamic control.
		/// </summary>
		public void SetServer(McpServer server) {
			mcpServer = server;
		}

		void McpSettingsImpl_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			// Save settings to persistent storage
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(EnableServer), EnableServer);
			sect.Attribute(nameof(Host), Host);
			sect.Attribute(nameof(Port), Port);

			// Handle server enable/disable dynamically (no restart required)
			if (e.PropertyName == nameof(EnableServer) && mcpServer != null) {
				if (EnableServer) {
					Log("Starting MCP server");
					mcpServer.Start();
				} else {
					Log("Stopping MCP server");
					mcpServer.Stop();
				}
			}
		}
	}
}
