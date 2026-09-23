using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Extension.MCP.Headless {
	/// <summary>
	/// Headless MCP host: serves the extension's tools over the MCP stdio transport, without the dnSpy
	/// window, so an MCP client can start it on demand the way it starts any stdio server. dnSpy's
	/// decompiler and metadata assemblies come from the installation this exe is deployed into.
	/// </summary>
	static class Program {
		static int Main(string[] args) {
			// stdout is the JSON-RPC channel: keep the real stream for the protocol and point Console.Out
			// at stderr, so a stray Console.WriteLine (ours or a library's) can never corrupt the stream.
			var protocolOut = Console.OpenStandardOutput();
			Console.SetOut(Console.Error);

			HeadlessOptions options;
			try {
				options = HeadlessOptions.Parse(args);
			}
			catch (ArgumentException ex) {
				Console.Error.WriteLine($"error: {ex.Message}");
				Console.Error.WriteLine();
				HeadlessOptions.WriteUsage(Console.Error);
				return 2;
			}
			if (options.ShowHelp) {
				HeadlessOptions.WriteUsage(Console.Error);
				return 0;
			}

			var binDir = AssemblyResolution.FindDnSpyBinDirectory(options.DnSpyDirectory, AppContext.BaseDirectory);
			if (binDir is null) {
				Console.Error.WriteLine(options.DnSpyDirectory is null
					? "error: couldn't find dnSpy. Deploy this exe next to dnSpy.Console.exe, or pass --dnspy <dnSpy folder>."
					: $"error: no dnSpy installation in '{options.DnSpyDirectory}' (looked for dnSpy.Contracts.DnSpy.dll and dnSpy.Decompiler.ILSpy.Core.dll, also under bin\\).");
				return 2;
			}
			AssemblyResolution.Install(AppContext.BaseDirectory, binDir);
			return Run(options, binDir, protocolOut);
		}

		// Kept out of Main, and never inlined into it, so that no dnSpy / dnlib / extension type is
		// touched before the assembly resolver is installed.
		[MethodImpl(MethodImplOptions.NoInlining)]
		static int Run(HeadlessOptions options, string binDir, Stream protocolOut) {
			if (options.ShowVersion) {
				using (var writer = new StreamWriter(protocolOut))
					writer.WriteLine(McpDispatcher.ServerVersion);
				return 0;
			}

			var settings = new McpSettings();
			if (!options.Quiet)
				settings.Logged += line => Console.Error.WriteLine(line);

			IDecompiler decompiler;
			try {
				decompiler = HeadlessMcpHost.LoadCSharpDecompiler();
			}
			catch (Exception ex) {
				Console.Error.WriteLine($"error: couldn't load dnSpy's C# decompiler from {binDir}: {ex.GetType().Name}: {ex.Message}");
				return 1;
			}

			var host = new HeadlessMcpHost(decompiler);
			var tools = new McpTools(host, settings);
			var dispatcher = new McpDispatcher(tools, new BepInExResources(settings), settings);
			settings.Log($"dnSpy MCP headless host {McpDispatcher.ServerVersion}, using dnSpy in {binDir}");

			if (options.OpenPaths.Count > 0)
				Preload(tools, settings, options.OpenPaths);

			return new StdioTransport(dispatcher, settings).Run(Console.OpenStandardInput(), protocolOut);
		}

		// Goes through the open_files tool itself, so a preload behaves exactly like the client asking.
		static void Preload(McpTools tools, McpSettings settings, List<string> paths) {
			var result = tools.ExecuteTool("open_files", new Dictionary<string, object> { ["paths"] = paths });
			var text = result.Content.Count > 0 ? result.Content[0].Text : string.Empty;
			if (result.IsError) {
				settings.Log($"preload failed: {text}");
				return;
			}
			using (var json = JsonDocument.Parse(text)) {
				var root = json.RootElement;
				settings.Log($"preloaded {root.GetProperty("loaded_count").GetInt32()} file(s), " +
					$"{root.GetProperty("failed_count").GetInt32()} failed");
				foreach (var failure in root.GetProperty("failed").EnumerateArray())
					settings.Log($"  could not load {failure.GetProperty("path").GetString()}: {failure.GetProperty("error").GetString()}");
			}
		}
	}

	/// <summary>Command-line options. Plain BCL code: parsed before any dnSpy assembly can load.</summary>
	sealed class HeadlessOptions {
		public string? DnSpyDirectory { get; private set; }
		public List<string> OpenPaths { get; } = new List<string>();
		public bool Quiet { get; private set; }
		public bool ShowHelp { get; private set; }
		public bool ShowVersion { get; private set; }

		public static HeadlessOptions Parse(string[] args) {
			var options = new HeadlessOptions();
			for (int i = 0; i < args.Length; i++) {
				var arg = args[i];
				switch (arg) {
				case "--dnspy":
					options.DnSpyDirectory = NextValue(args, ref i, arg);
					break;
				case "--open":
					options.OpenPaths.Add(NextValue(args, ref i, arg));
					break;
				case "-q":
				case "--quiet":
					options.Quiet = true;
					break;
				case "--version":
					options.ShowVersion = true;
					break;
				case "-h":
				case "--help":
				case "/?":
					options.ShowHelp = true;
					break;
				default:
					if (arg.StartsWith("-", StringComparison.Ordinal))
						throw new ArgumentException($"unknown option '{arg}'");
					options.OpenPaths.Add(arg);
					break;
				}
			}
			return options;
		}

		static string NextValue(string[] args, ref int i, string option) {
			if (i + 1 >= args.Length)
				throw new ArgumentException($"{option} needs a value");
			return args[++i];
		}

		public static void WriteUsage(TextWriter writer) {
			writer.WriteLine("dnSpy MCP headless host: serves the dnSpy MCP extension's tools over the MCP stdio");
			writer.WriteLine("transport, without the dnSpy window. MCP clients launch it on demand.");
			writer.WriteLine();
			writer.WriteLine("Usage: dnSpy.Extension.MCP.Headless.exe [options] [<file or folder> ...]");
			writer.WriteLine();
			writer.WriteLine("  <file or folder>   .NET assemblies to load at startup (a folder loads its *.dll), like open_files");
			writer.WriteLine("  --open <path>      same as a positional <file or folder>");
			writer.WriteLine("  --dnspy <folder>   the dnSpy installation to use (default: the one this exe is deployed in)");
			writer.WriteLine("  -q, --quiet        don't log to stderr");
			writer.WriteLine("  --version          print the version and exit");
			writer.WriteLine("  -h, --help         show this help");
		}
	}
}
