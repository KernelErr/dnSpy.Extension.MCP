using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// User control for MCP server settings UI.
	/// </summary>
	public partial class McpSettingsControl : UserControl {
		/// <summary>
		/// Initializes the settings control.
		/// </summary>
		public McpSettingsControl() => InitializeComponent();

		/// <summary>
		/// Handles the copy logs button click, copying all logs to clipboard with retry logic.
		/// </summary>
		void CopyLogsButton_Click(object sender, RoutedEventArgs e) {
			try {
				if (DataContext is SettingsViewModel viewModel && viewModel.LogMessages != null) {
					var allLogs = string.Join(Environment.NewLine, viewModel.LogMessages);
					if (string.IsNullOrEmpty(allLogs)) {
						MessageBox.Show("No logs to copy.",
							"No Logs",
							MessageBoxButton.OK,
							MessageBoxImage.Information);
						return;
					}

					// Try to copy to clipboard with retries
					bool copied = TryCopyToClipboard(allLogs);

					if (copied) {
						MessageBox.Show($"Copied {viewModel.LogMessages.Count} log messages to clipboard!",
							"Logs Copied",
							MessageBoxButton.OK,
							MessageBoxImage.Information);
					} else {
						// Fallback: Show logs in a window
						ShowLogsWindow(allLogs, viewModel.LogMessages.Count);
					}
				} else {
					MessageBox.Show("Unable to access log messages.",
						"Error",
						MessageBoxButton.OK,
						MessageBoxImage.Warning);
				}
			}
			catch (Exception ex) {
				MessageBox.Show($"Failed to access logs: {ex.Message}",
					"Error",
					MessageBoxButton.OK,
					MessageBoxImage.Error);
			}
		}

		bool TryCopyToClipboard(string text) {
			// Try multiple times with delays
			for (int i = 0; i < 5; i++) {
				try {
					Clipboard.SetDataObject(text, true);
					return true;
				}
				catch (System.Runtime.InteropServices.COMException) {
					// Clipboard is locked, wait and retry
					Thread.Sleep(100);
				}
				catch {
					// Other error, give up
					return false;
				}
			}
			return false;
		}

		void ShowLogsWindow(string logs, int count) {
			var window = new Window {
				Title = $"MCP Server Logs ({count} messages)",
				Width = 800,
				Height = 600,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				Owner = Window.GetWindow(this)
			};

			var grid = new Grid();
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			var textBox = new TextBox {
				Text = logs,
				IsReadOnly = true,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				FontFamily = new System.Windows.Media.FontFamily("Consolas"),
				FontSize = 11,
				TextWrapping = TextWrapping.NoWrap,
				Margin = new Thickness(10)
			};
			Grid.SetRow(textBox, 0);
			grid.Children.Add(textBox);

			var buttonPanel = new StackPanel {
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(10)
			};
			Grid.SetRow(buttonPanel, 1);

			var copyButton = new Button {
				Content = "Try Copy Again",
				Padding = new Thickness(20, 5, 20, 5),
				Margin = new Thickness(5)
			};
			copyButton.Click += (s, e) => {
				if (TryCopyToClipboard(logs)) {
					MessageBox.Show("Logs copied to clipboard successfully!",
						"Success",
						MessageBoxButton.OK,
						MessageBoxImage.Information);
					window.Close();
				} else {
					MessageBox.Show("Clipboard is still locked. Try closing other applications that might be using the clipboard.",
						"Clipboard Locked",
						MessageBoxButton.OK,
						MessageBoxImage.Warning);
				}
			};
			buttonPanel.Children.Add(copyButton);

			var selectAllButton = new Button {
				Content = "Select All (Ctrl+C to copy)",
				Padding = new Thickness(20, 5, 20, 5),
				Margin = new Thickness(5)
			};
			selectAllButton.Click += (s, e) => {
				textBox.SelectAll();
				textBox.Focus();
			};
			buttonPanel.Children.Add(selectAllButton);

			var closeButton = new Button {
				Content = "Close",
				Padding = new Thickness(20, 5, 20, 5),
				Margin = new Thickness(5)
			};
			closeButton.Click += (s, e) => window.Close();
			buttonPanel.Children.Add(closeButton);

			grid.Children.Add(buttonPanel);
			window.Content = grid;

			window.ShowDialog();
		}
	}
}
