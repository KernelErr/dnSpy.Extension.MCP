using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// Provider for the MCP server settings page in dnSpy settings dialog.
	/// </summary>
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class McpAppSettingsPageProvider : IAppSettingsPageProvider {
		readonly McpSettings mcpSettings;

		/// <summary>
		/// Initializes the settings page provider.
		/// </summary>
		[ImportingConstructor]
		McpAppSettingsPageProvider(McpSettings mcpSettings) => this.mcpSettings = mcpSettings;

		/// <summary>
		/// Creates the settings page.
		/// </summary>
		public IEnumerable<AppSettingsPage> Create() {
			yield return new McpAppSettingsPage(mcpSettings);
		}
	}

	/// <summary>
	/// Settings page for the MCP server in dnSpy settings dialog.
	/// </summary>
	sealed class McpAppSettingsPage : AppSettingsPage {
		static readonly Guid THE_GUID = new Guid("B9E3F7A1-4D2C-4E8B-9F1A-3C5E7B9D4F2A");

		/// <summary>
		/// Gets the parent settings page GUID (none for top-level page).
		/// </summary>
		public override Guid ParentGuid => Guid.Empty;

		/// <summary>
		/// Gets the unique GUID for this settings page.
		/// </summary>
		public override Guid Guid => THE_GUID;

		/// <summary>
		/// Gets the display order in the settings tree.
		/// </summary>
		public override double Order => AppSettingsConstants.ORDER_DEBUGGER + 0.2;

		/// <summary>
		/// Gets the page title displayed in settings.
		/// </summary>
		public override string Title => "MCP Server";

		/// <summary>
		/// Gets the icon displayed next to the page title.
		/// </summary>
		public override ImageReference Icon => DsImages.MarkupTag;

		/// <summary>
		/// Gets the UI control for this settings page.
		/// </summary>
		public override object? UIObject {
			get {
				if (uiObject is null) {
					uiObject = new McpSettingsControl();
					// Use a wrapper that combines editable settings with live logs from global settings
					uiObject.DataContext = new SettingsViewModel(newSettings, globalSettings);
				}
				return uiObject;
			}
		}
		McpSettingsControl? uiObject;

		readonly McpSettings globalSettings;
		readonly McpSettings newSettings;

		/// <summary>
		/// Initializes the settings page with the given settings instance.
		/// </summary>
		public McpAppSettingsPage(McpSettings mcpSettings) {
			globalSettings = mcpSettings;
			newSettings = mcpSettings.Clone();
		}

		/// <summary>
		/// Applies the settings when user clicks OK.
		/// </summary>
		public override void OnApply() =>
			newSettings.CopyTo(globalSettings);

		/// <summary>
		/// Called when the settings dialog is closed.
		/// </summary>
		public override void OnClosed() {
		}
	}

	/// <summary>
	/// View model for MCP settings that provides editable settings while showing live logs from global settings.
	/// This allows users to see real-time logs even before applying settings changes.
	/// </summary>
	public class SettingsViewModel : dnSpy.Contracts.MVVM.ViewModelBase {
		readonly McpSettings editableSettings;
		readonly McpSettings globalSettings;

		/// <summary>
		/// Initializes the view model with editable and global settings instances.
		/// </summary>
		public SettingsViewModel(McpSettings editable, McpSettings global) {
			editableSettings = editable;
			globalSettings = global;

			// Forward property change notifications from editable settings
			editableSettings.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName ?? string.Empty);

			// Forward property change notifications from global settings (for live logs)
			globalSettings.PropertyChanged += (s, e) => {
				if (e.PropertyName == nameof(LogText) || e.PropertyName == nameof(LogMessages)) {
					OnPropertyChanged(e.PropertyName);
				}
			};
		}

		/// <summary>
		/// Gets or sets whether the MCP server is enabled (editable).
		/// </summary>
		public bool EnableServer {
			get => editableSettings.EnableServer;
			set => editableSettings.EnableServer = value;
		}

		/// <summary>
		/// Gets or sets the server host (editable).
		/// </summary>
		public string Host {
			get => editableSettings.Host;
			set => editableSettings.Host = value;
		}

		/// <summary>
		/// Gets or sets the server port (editable).
		/// </summary>
		public int Port {
			get => editableSettings.Port;
			set => editableSettings.Port = value;
		}

		/// <summary>
		/// Gets the live log messages from global settings.
		/// </summary>
		public System.Collections.ObjectModel.ObservableCollection<string> LogMessages => globalSettings.LogMessages;

		/// <summary>
		/// Gets the live combined log text from global settings.
		/// </summary>
		public string LogText => globalSettings.LogText;
	}
}
