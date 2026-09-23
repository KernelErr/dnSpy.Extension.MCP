using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.PE;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;

namespace dnSpy.Extension.MCP.Headless {
	/// <summary>
	/// <see cref="IMcpHost"/> without dnSpy's UI: a plain list of loaded documents, dnSpy's C# decompiler
	/// loaded the way dnSpy.Console.exe loads it, and no-op UI hooks. The documents are the same
	/// <see cref="IDsDocument"/> types dnSpy's document service holds, so every tool works unchanged.
	/// </summary>
	sealed class HeadlessMcpHost : IMcpHost {
		readonly object documentsLock = new object();
		readonly List<IDsDocument> documents = new List<IDsDocument>();
		readonly AssemblyResolver assemblyResolver;
		readonly ModuleContext moduleContext;

		public IDecompiler Decompiler { get; }

		public HeadlessMcpHost(IDecompiler decompiler) {
			Decompiler = decompiler;
			// The resolver settings dnSpy.exe and dnSpy.Console.exe use.
			assemblyResolver = new AssemblyResolver {
				EnableFrameworkRedirect = false,
				FindExactMatch = true,
				EnableTypeDefCache = true,
			};
			moduleContext = DsDotNetDocumentBase.CreateModuleContext(assemblyResolver);
		}

		/// <summary>
		/// dnSpy's C# decompiler, obtained exactly the way dnSpy.Console.exe gets its languages: no MEF,
		/// just the <see cref="IDecompilerProvider"/>s in dnSpy.Decompiler.ILSpy.Core.
		/// </summary>
		public static IDecompiler LoadCSharpDecompiler() {
			var assembly = Assembly.Load("dnSpy.Decompiler.ILSpy.Core");
			var decompilers = assembly.GetTypes()
				.Where(t => !t.IsAbstract && !t.IsInterface && typeof(IDecompilerProvider).IsAssignableFrom(t))
				.SelectMany(t => ((IDecompilerProvider)Activator.CreateInstance(t)!).Create());
			return decompilers.FirstOrDefault(d => d.GenericGuid == DecompilerConstants.LANGUAGE_CSHARP)
				?? throw new InvalidOperationException("dnSpy.Decompiler.ILSpy.Core provides no C# decompiler");
		}

		public IDsDocument[] GetDocuments() {
			lock (documentsLock)
				return documents.ToArray();
		}

		public IDsDocument? OpenDocument(string path) {
			lock (documentsLock) {
				var existing = documents.FirstOrDefault(d => string.Equals(d.Filename, path, StringComparison.OrdinalIgnoreCase));
				if (existing != null)
					return existing;
			}

			// Read the file into memory rather than memory-mapping it (dnSpy's UseMemoryMappedIO=false
			// path): this process lives as long as the MCP client and must not lock the target's DLLs,
			// which the user may want to rebuild or replace meanwhile.
			var peImage = new PEImage(File.ReadAllBytes(path), path);
			// The same ".NET or not" test dnSpy's document service applies: a CLI header directory.
			if (peImage.ImageNTHeaders.OptionalHeader.DataDirectories[14].VirtualAddress == 0) {
				peImage.Dispose();
				return null;
			}
			var module = ModuleDefMD.Load(peImage, new ModuleCreationOptions(moduleContext) { TryToLoadPdbFromDisk = false });
			var info = DsDocumentInfo.CreateDocument(path);
			IDsDocument document = module.Assembly != null
				? DsDotNetDocument.CreateAssembly(info, module, loadSyms: true)
				: DsDotNetDocument.CreateModule(info, module, loadSyms: true);

			lock (documentsLock)
				documents.Add(document);
			// Lets references between loaded assemblies resolve to these instances, as in dnSpy.
			assemblyResolver.AddToCache(module);
			// Loads the PDB, as dnSpy's document service does when it adds a document.
			(document as IDsDocument2)?.OnAdded();
			return document;
		}

		// There is no UI thread: every tool runs inline on the stdio loop.
		public T InvokeOnUiThread<T>(Func<T> action) => action();

		public void ApplyRename(IMemberDef definition, ModuleDef module, Action rename, Action rollback, string operation) {
			try {
				rename();
			}
			catch {
				rollback();
				throw;
			}
		}

		// Nothing is displayed, so there is nothing to refresh.
		public void RefreshTreeNode(IMemberDef definition, string operation) { }

		public void RefreshDecompiledViews(ModuleDef module, string operation) { }
	}
}
