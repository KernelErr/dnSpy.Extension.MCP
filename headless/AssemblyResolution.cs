using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace dnSpy.Extension.MCP.Headless {
	/// <summary>
	/// Finds the dnSpy installation and resolves assemblies from it. BCL types only: this runs before
	/// any dnSpy, dnlib or extension assembly can be loaded.
	/// </summary>
	static class AssemblyResolution {
		// Where dnSpy loads this extension from, relative to its BinDirectory.
		static readonly string ExtensionFolder = Path.Combine("Extensions", "dnSpy.Extension.MCP");

		/// <summary>
		/// dnSpy's BinDirectory — the folder holding dnSpy.Contracts.DnSpy.dll, which is how dnSpy itself
		/// defines it — of the installation in <paramref name="explicitDir"/> (a dnSpy root or bin folder),
		/// or, when that is null, of the installation this exe is deployed in: <paramref name="baseDir"/>
		/// or one of its parents, each also tried with a bin\ subfolder. Null if none is found.
		/// </summary>
		public static string? FindDnSpyBinDirectory(string? explicitDir, string baseDir) {
			var roots = explicitDir != null ? new[] { Path.GetFullPath(explicitDir) } : Ancestors(baseDir);
			foreach (var root in roots) {
				foreach (var candidate in new[] { root, Path.Combine(root, "bin") }) {
					// The decompiler assembly too: a build output next to copied contract DLLs isn't dnSpy.
					if (File.Exists(Path.Combine(candidate, "dnSpy.Contracts.DnSpy.dll")) &&
						File.Exists(Path.Combine(candidate, "dnSpy.Decompiler.ILSpy.Core.dll")))
						return candidate;
				}
			}
			return null;
		}

		static IEnumerable<string> Ancestors(string dir) {
			for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
				yield return d.FullName;
		}

		/// <summary>
		/// Resolves assemblies the runtime can't find on its own from, in order: this exe's folder,
		/// dnSpy's bin, and the extension's folder under bin\Extensions (where dnSpy.Extension.MCP.x.dll
		/// is deployed). AppDomain.AssemblyResolve fires on both .NET Framework and .NET, so one handler
		/// covers both target frameworks.
		/// </summary>
		public static void Install(string baseDir, string binDir) {
			var dirs = new[] { baseDir, binDir, Path.Combine(binDir, ExtensionFolder) };
			AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
				var name = new AssemblyName(e.Name).Name;
				// Satellite resource lookups are expected to miss; let the runtime fall back.
				if (string.IsNullOrEmpty(name) || name!.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
					return null;
				foreach (var dir in dirs) {
					var path = Path.Combine(dir, name + ".dll");
					if (File.Exists(path))
						return Assembly.LoadFrom(path);
				}
				return null;
			};
		}
	}
}
