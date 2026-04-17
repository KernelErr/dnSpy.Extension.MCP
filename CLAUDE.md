# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository overview

This is the **dnSpy MCP extension** — a standalone git repo that is cloned *into* a [dnSpyEx](https://github.com/dnSpyEx/dnSpy) checkout at `Extensions/dnSpy.Extension.MCP/`. It is a dnSpy extension (MEF-loaded `*.x.dll`) that runs an HTTP server inside the dnSpy process and exposes 15 assembly-analysis / decompilation / IL-editing tools and 6 embedded BepInEx docs to AI assistants over Model Context Protocol (JSON-RPC 2.0).

## Build

**Prerequisite: a dnSpyEx checkout with submodules initialized.** The csproj has two `ProjectReference`s (`../../dnSpy/dnSpy.Contracts.DnSpy/` and `../../dnSpy/dnSpy.Contracts.Logic/`) and imports `../../DnSpyCommon.props`. Without the parent checkout and `git submodule update --init --recursive` in it, restore fails.

Target frameworks are inherited from `../../DnSpyCommon.props` (currently `net48;net10.0-windows`). Don't pin TFMs in this csproj — let the common props drive it.

```bash
dotnet build -c Release                     # both TFMs (~3s; only the two Contracts refs)
dotnet build -c Debug -f net48              # single-TFM iteration
dotnet build -c Debug -f net10.0-windows
```

Output: `bin/<Config>/<TFM>/dnSpy.Extension.MCP.x.dll`. The **`.x` suffix is mandatory** — dnSpy's extension loader skips files without it. Deploy by copying the DLL into `<dnSpy-Install>\bin\Extensions\dnSpy.Extension.MCP\` — the folder name must match the DLL stem. Placing the DLL one level up under `bin\Extensions\` silently fails.

There is no test suite. CI (`.github/workflows/build.yml`) just builds both TFMs; `release.yml` runs the parent repo's `build.ps1` and ships all-in-one zips on `v*.*.*` tags.

## Architecture

- **`TheExtension.cs`** — `[ExportExtension]` MEF entry. Starts the server on `ExtensionEvent.Loaded` (if enabled), stops it on `AppExit`.
- **`McpServer.cs`** — single `HttpListener` on **both TFMs**. Kestrel was intentionally dropped: dnSpy's self-contained .NET bundle does not include ASP.NET Core, so referencing `Microsoft.AspNetCore.*` causes a silent `TypeLoadException` during MEF composition and the `IExtension` part never instantiates. Do not reintroduce Kestrel or `#if NET` branches here. The server exposes four routes on one port:
  - `GET /health` — liveness probe
  - `POST /` — plain HTTP JSON-RPC (one-shot request/response)
  - `GET /sse` — opens a long-lived `text/event-stream`; first event carries the `/message?sessionId=<id>` URL
  - `POST /message?sessionId=<id>` — accepts JSON-RPC, returns `202 Accepted`, writes the real response back over the SSE stream
  - Port fallback: tries `Port..Port+19` and logs which one it bound to. Clients should read the resolved port from the log.
- **`McpTools.cs` / `McpTools.IL.cs`** — the 15 tools, wrapping `IDocumentTreeView` (loaded-assembly enumeration), `IDecompilerService` (decompilation), and dnlib's `CilBody` (IL view/edit). `McpTools` is a `sealed partial class` split across two files: `McpTools.cs` holds the MEF export, tool schemas, dispatch switch, and shared helpers (`FindAssemblyByName`, `FindTypeInAssembly`, `FindMethod`, cursor codec); `McpTools.IL.cs` holds the IL view/patch/save handlers and the tagged-operand renderer/parser. List-returning tools use **opaque base64 cursors** (see `EncodeCursor` / `DecodeCursor`) with default page size **10** to stay within AI token budgets. Follow this pattern when adding list tools.
- **`ExecuteTool` marshals every handler onto the WPF UI thread via `InvokeOnUiThread`.** `IDocumentTreeView` nodes are `DispatcherObject` instances — reading the tree from an HTTP worker throws "calling thread cannot access this object" the moment a user-loaded assembly is indexed. Handlers that already marshal explicitly (patch, revert, save) double-wrap harmlessly because `InvokeOnUiThread` short-circuits when `CheckAccess()` is true.
- **`McpProtocol.cs`** — JSON-RPC 2.0 + MCP DTOs. Uses `System.Text.Json` on both TFMs (BCL on net10, NuGet on net48). `jsonOptions` in `McpServer` writes with `JsonIgnoreCondition.WhenWritingNull` — MCP requires null-stripped payloads.
- **`McpSettings.cs`** — two classes: `McpSettings` (public `ViewModelBase`, the imported service) and `McpSettingsImpl : McpSettings` (the `[Export(typeof(McpSettings))]` impl). The impl persists via `ISettingsService` under a **hardcoded settings GUID** — never change it, or every user's saved settings reset. Property changes drive server start/stop, so toggling "Enable Server" in the UI takes effect without a restart.
- **`McpSettingsPage.cs` / `McpSettingsControl.xaml*`** — WPF settings page wired into dnSpy via `IAppSettingsPageProvider`.
- **`BepInExResources.cs`** — embedded markdown docs served via MCP `resources/list` / `resources/read`. Self-contained, no network.

### Conventions specific to this extension

- **MEF everywhere.** New services use `[Export(typeof(T))]` + `[ImportingConstructor]`. Never `new` up `McpServer`, `McpSettings`, `McpTools`, or `BepInExResources` — let the container wire them.
- **JSON-RPC error codes.** In `McpServer.HandleRequest`, `ArgumentException` → `-32602` (invalid params), everything else → `-32603` (internal error). Throw `ArgumentException` from tool handlers for bad user input so clients get the right code.
- **No `#if` TFM branches.** The codebase is currently single-stack (HttpListener + System.Text.Json on both TFMs). The only TFM-conditional item is the `System.Text.Json` `PackageReference` in the csproj under `IsDotNetFramework`.
- **`McpSettings.Log` mirrors to disk.** The on-disk log at `McpSettings.LogFilePath` is the authoritative record — the in-memory `ObservableCollection` is best-effort for the UI only, and can drop entries when the dispatcher isn't yet up. `LogFilePath` is currently hardcoded (`D:\dnspy-mcp.log`); if you port this to another machine, expect the log to go missing silently unless you change the path.
- **UI-thread marshaling for log.** `Log()` uses `Application.Current.Dispatcher` because `LogMessages` is bound to WPF. Don't bypass it.

### IL editing (list_methods / get_method_il / patch_method_il / revert_method_il / save_assembly)

- **Method identification.** Names alone aren't unique — pass `parameter_types` (array of `FullName`s) or the `method_token` (dnlib `MDToken.Raw`) returned by `list_methods` / `get_type_info`. `FindMethod` throws `ArgumentException` with a candidate-signature list on ambiguity so the AI can retry.
- **Operand grammar is tagged** (`int:`, `str:`, `method:`, `field:`, `type:`, `token:method:|field:|type:`, `label:`, `switch:[…]`, `local:`, `arg:`, `int8:`, `uint8:`, `long:`, `float:`, `double:`). The renderer and parser in `McpTools.IL.cs` share this grammar — keep them symmetric when adding kinds. `InlineSig` / `calli` is unsupported.
- **Edit semantics.** `replace` mutates the existing `Instruction` object in place so branch / switch operands that reference it still resolve correctly. `insert` creates a new `Instruction` and places it before the target index (or appends if index == Count). `delete` removes by reference. All edit indices refer to the pre-batch state — `ResolveEdit` resolves them to `Instruction` references before applying so later inserts/deletes don't shift them.
- **Snapshots preserve identity.** `CilBodySnapshot` captures `(Instruction reference, OpCode, Operand)` per instruction plus the ordered list. `revert_method_il` un-mutates each `Instruction` back to its snapshotted (OpCode, Operand) and re-adds them in order — this is mandatory because a reference-only snapshot would be poisoned by the in-place `replace` mutation. `Instruction[]` switch operands are cloned at snapshot time for the same reason.
- **Save uses `NativeWrite` for `ModuleDefMD`, `Write` for everything else.** Plain `Write` on a module that was loaded from disk drops native stubs, Win32 resources, delay-loaded imports, and mixed-mode metadata; `NativeModuleWriterOptions(md, optimizeImageSize: true)` preserves them. Before writing, memory-mapped I/O is disabled on every `IDsDocument` whose filename matches the target (case-insensitive) via the public `peImage as dnlib.PE.IInternalPEImage` cast — `dnSpy.AsmEditor`'s `IMmapDisabler` is internal so we inline the one-liner. GAC paths are refused via `GacInfo.IsGacPath`.
- **Backup-then-overwrite on same-path saves.** When `output_path` is omitted (or equals the original location), `save_assembly` copies the existing file to `<target>.<yyyyMMdd-HHmmss>.bak` before writing. Side-path saves leave the original untouched and return `backup_path = null`.
- **dnSpy's in-memory tree is NOT refreshed after save.** The `ModuleDef` our patches live in is still the pre-patch one in memory; the user has to reopen the assembly in dnSpy to see saved-to-disk state. This is deliberate — refreshing would require re-composing tabs/nodes under AsmEditor internals we don't reference.
- **No Ctrl+Z integration.** `patch_method_il` does not route through dnSpy's `IUndoCommandService` (it's internal to AsmEditor). Use `revert_method_il` for undo within an MCP session; the snapshot is dropped after revert or after a successful save.

### Test fixture (`tests/fixtures/`)

- `TestIL.cs` + `TestIL.csproj` — a tiny netstandard2.0 assembly covering overloads, constant/string/branch/field/type operands. Checked in; the built `TestIL.dll` is **not** (see `.gitignore`).
- `build-fixture.ps1` — `dotnet build` wrapper that emits `bin/TestIL.dll` and prints its SHA256.
- `run-tests.ps1` — end-to-end driver. Builds the fixture and the extension, deploys the `.x.dll` into dnSpy's Extensions folder, sets `Port=3100` in `%APPDATA%\dnSpy\dnSpy.xml` (because WSL's `wslrelay.exe` commonly holds 3000 even though our `TcpListener` probe reports it free), launches dnSpy with the fixture as CLI arg, polls `list_assemblies` until `TestIL` appears, then walks through list_methods / decompile_method / get_method_il / patch_method_il / revert_method_il / save_assembly with SHA256 and behavioural asserts (a spawned PowerShell loads the saved DLL and invokes `AddOne(10)` to confirm the +1 → +41 patch took effect on disk).
- The extension csproj sets `<DefaultItemExcludes>$(DefaultItemExcludes);tests/**</DefaultItemExcludes>` so the fixture's generated `AssemblyInfo.cs` doesn't get pulled into the extension build.

## Git

This repo is tracked independently from the parent dnSpy checkout. Work happens on `main`. Releases are driven by `v*.*.*` tags.
