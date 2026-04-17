# dnSpy MCP Extension

[![Build](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml)
[![Release](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml)

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) extension for [dnSpyEx](https://github.com/dnSpyEx/dnSpy) that exposes .NET assembly **analysis** and **IL-editing** tools to AI assistants like Claude.

Chinese / 中文说明: see [README.zh-CN.md](README.zh-CN.md).

## Features

### MCP Tools (15 total)

**Analysis & navigation**
1. **list_assemblies** — list all loaded assemblies with metadata
2. **get_assembly_info** — detailed info about a specific assembly (paginated namespaces)
3. **list_types** — all types in an assembly or namespace (paginated)
4. **get_type_info** — fields, properties, and paginated methods for a type (methods include `token` / `MDToken` for unambiguous identification)
5. **list_methods** — methods of a type with `token` + `parameter_types` per entry, paginated
6. **get_type_fields** — filter fields by wildcard pattern (e.g. `*Bonus*`)
7. **get_type_property** — detailed info about a property including getter/setter
8. **search_types** — wildcard / substring type search across all assemblies
9. **find_path_to_type** — BFS over fields/properties to connect two types
10. **decompile_method** — decompile a method to C# (accepts `parameter_types` / `method_token` to disambiguate overloads)

**IL viewing & editing** (new in 0.1.3)
11. **get_method_il** — instructions (index, offset, opcode, operand) + locals + exception handlers + body flags
12. **patch_method_il** — ordered `replace` / `insert` / `delete` / `set_init_locals` edits; snapshot-on-first-patch
13. **revert_method_il** — restore the pre-patch body shape
14. **save_assembly** — write the module to disk (timestamped backup on overwrite, `NativeWrite` preserves native stubs / Win32 resources / delay-loaded imports, GAC refused)

**Codegen**
15. **generate_bepinex_plugin** — BepInEx plugin template with Harmony hooks

### MCP Resources (6 total)

Embedded BepInEx documentation served over `resources/list` / `resources/read`:

1. **plugin-structure**
2. **harmony-patching** (Prefix / Postfix / Transpiler)
3. **configuration**
4. **common-scenarios**
5. **il2cpp-guide**
6. **mono-vs-il2cpp**

All docs ship inside the DLL — no network required.

## IL viewing and editing

See, patch, and save bytecode from an AI client. Mirrors the dnSpy *Edit Method Body* dialog.

### Operand grammar

Each instruction's operand is a single tagged string; the same grammar is used by `get_method_il` (read) and `patch_method_il` (write), so operands round-trip unchanged.

| Tag | Example | Opcodes |
|-----|---------|---------|
| `int:` / `int8:` / `uint8:` / `long:` | `int:42` | `ldc.i4`, `ldc.i4.s`, `ldc.i8` |
| `float:` / `double:` | `double:3.14` | `ldc.r4`, `ldc.r8` |
| `str:` *(JSON-quoted)* | `str:"hello\n"` | `ldstr` |
| `method:` *(dnlib FullName)* | `method:System.Void Ns.T::M(System.Int32)` | `call`, `callvirt`, `newobj`, `ldftn`, `ldvirtftn`, `jmp` |
| `field:` | `field:System.Int32 Ns.T::F` | `ldfld`, `stfld`, `ldsfld`, `stsfld`, `ldflda`, `ldsflda` |
| `type:` | `type:System.String` | `castclass`, `isinst`, `box`, `unbox`, `newarr`, `initobj`, `ldelem*`, `stelem*`, … |
| `token:method:…` / `token:field:…` / `token:type:…` | `token:type:System.String` | `ldtoken` |
| `label:<idx>` | `label:7` | `br`, `brtrue.s`, `blt`, … |
| `switch:[<i>,<i>,…]` | `switch:[3,7,12]` | `switch` |
| `local:<idx>` | `local:0` | `ldloc*`, `stloc*` |
| `arg:<idx>` | `arg:1` | `ldarg*`, `starg*` |
| *(empty)* | `""` | no operand (`ldarg.0`, `add`, `ret`, …) |

`calli` / `InlineSig` is not supported in 0.1.3.

### End-to-end: patch a constant and persist

Assume `TestIL.dll` contains `public static int AddOne(int x) => x + 1;`.

```bash
# 1. Find the method (parameter_types disambiguates overloads).
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" -d '{
  "jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"list_methods",
    "arguments":{"assembly_name":"TestIL","type_full_name":"TestIL.Simple"}}}'

# 2. Read the IL.
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" -d '{
  "jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"get_method_il",
    "arguments":{"assembly_name":"TestIL","type_full_name":"TestIL.Simple","method_name":"AddOne"}}}'
# Instructions include: {"index":1,"opcode":"ldc.i4.1","operand":""}

# 3. Replace the +1 with +41.
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" -d '{
  "jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"patch_method_il",
    "arguments":{"assembly_name":"TestIL","type_full_name":"TestIL.Simple","method_name":"AddOne",
      "edits":[{"op":"replace","index":1,"opcode":"ldc.i4","operand":"int:41"}]}}}'

# 4. Save. Original file is backed up to <path>.<yyyyMMdd-HHmmss>.bak first.
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" -d '{
  "jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"save_assembly",
    "arguments":{"assembly_name":"TestIL"}}}'
```

Reload the saved DLL in a fresh process and `AddOne(10)` returns **`51`** instead of **`11`**.

### Caveats

- **No Ctrl+Z.** `patch_method_il` does not route through dnSpy's undo stack. Use `revert_method_il` — the snapshot is taken the first time a given method is patched, and dropped after revert or after a successful save.
- **dnSpy's in-memory view is not refreshed after save.** Reopen the assembly in dnSpy to see the saved state in the running instance.
- **GAC paths are refused.** Saving `mscorlib` etc. returns a `-32602` error.
- **Instruction-level only.** Adding / removing locals or exception handlers is out of scope for 0.1.3; `get_method_il` exposes them read-only.

## Installation

### Recommended: all-in-one zip

Head to [Releases](https://github.com/KernelErr/dnSpy.Extension.MCP/releases) and download the bundle that matches your system — **the extension is already placed inside, no paths to figure out**:

| File | Contents | Runtime requirement |
|------|----------|---------------------|
| `dnSpy-MCP-win-x64.zip` | dnSpy .NET 10 self-contained x64 + MCP extension | None — runtime is bundled |
| `dnSpy-MCP-win-x86.zip` | dnSpy .NET 10 self-contained x86 + MCP extension | None — runtime is bundled |
| `dnSpy-MCP-net48.zip` | dnSpy .NET Framework 4.8 build + MCP extension | .NET Framework 4.8 (default on Windows 10+) |

1. Download and unzip anywhere.
2. Double-click `dnSpy.exe`.
3. Open **Edit → Settings → MCP Server**, tick **Enable Server**, click OK.

That's it. If you already use dnSpy and just want the plugin, see "Plugin-only" below.

### Plugin-only (for users who already have dnSpy installed)

1. Download the DLL matching your dnSpy runtime:
   - `dnSpy.Extension.MCP-net48.dll` — .NET Framework 4.8 dnSpy
   - `dnSpy.Extension.MCP-net10.0-windows.dll` — .NET 10 dnSpy
2. Rename to `dnSpy.Extension.MCP.x.dll` (the `.x` suffix is required by dnSpy's extension loader).
3. Create the folder `dnSpy.Extension.MCP` under `<dnSpy-Install>\bin\Extensions\` and put the DLL inside.
4. Restart dnSpy.

**The final path must look exactly like this** — same folder name as the DLL stem, `.x.dll` suffix present, one level deep under `Extensions\`:

```
<dnSpy-Install>\
└── bin\
    └── Extensions\
        └── dnSpy.Extension.MCP\           ← folder (create if missing)
            └── dnSpy.Extension.MCP.x.dll  ← DLL with the .x suffix
```

Concrete example if dnSpy is installed at `C:\Tools\dnSpy`:

```
C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll
```

If the DLL ends up directly under `bin\Extensions\` (no subfolder), or without the `.x` suffix, dnSpy silently skips it and the MCP Server settings page will not appear.

### From source

```bash
# Clone dnSpyEx (submodules are required)
git clone --recursive https://github.com/dnSpyEx/dnSpy.git
cd dnSpy

# Clone this extension into the Extensions directory
git clone https://github.com/KernelErr/dnSpy.Extension.MCP.git Extensions/dnSpy.Extension.MCP

# Build (both TFMs)
cd Extensions/dnSpy.Extension.MCP
dotnet build -c Release

# Deploy
cp bin/Release/net10.0-windows/dnSpy.Extension.MCP.x.dll \
   <dnSpy-Install>/bin/Extensions/dnSpy.Extension.MCP/
```

## Configuration

Settings live under **Edit → Settings → MCP Server**:

- **Enable Server** — starts/stops the HTTP server immediately when toggled and applied.
- **Port** — preferred TCP port (default `3000`). If the port is already in use, the server automatically tries `port + 1`, up to 20 attempts, and logs which port it actually bound to. Check the Server Log pane for the resolved port.
- **Host** — bind address (default `localhost`).

## Transports

All three transports run on the same `HttpListener` on the same port. The server picks the right one by inspecting the path, HTTP method, and `Accept` header of each request.

### Streamable HTTP (MCP 2025-03-26)

Single-endpoint transport used by codex and other modern MCP clients. The client POSTs JSON-RPC requests with `Accept: application/json, text/event-stream`; the server returns the JSON-RPC response inline as `application/json` and allocates a session on `initialize` via the `Mcp-Session-Id` response header. Subsequent POSTs must echo that header. The server also honours `GET` on the same endpoint for server-initiated SSE and `DELETE` for teardown.

Both `/` and `/mcp` are accepted as the endpoint path.

```bash
# 1. Initialize — server returns the session ID in the Mcp-Session-Id header.
curl -i -X POST http://localhost:3000/ \
  -H "Accept: application/json, text/event-stream" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
# HTTP/1.1 200 OK
# Mcp-Session-Id: <sid>
# Content-Type: application/json
# {"jsonrpc":"2.0","id":1,"result":{...}}

# 2. Subsequent calls echo the session header.
curl -X POST http://localhost:3000/ \
  -H "Accept: application/json, text/event-stream" \
  -H "Content-Type: application/json" \
  -H "Mcp-Session-Id: <sid>" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'

# 3. Tear down explicitly (optional — the server also drops the session on shutdown).
curl -X DELETE http://localhost:3000/ -H "Mcp-Session-Id: <sid>"
```

Codex `~/.codex/config.toml`:

```toml
[mcp_servers.dnspy-mcp]
type = "streamable-http"
url = "http://localhost:3000"
```

### Plain HTTP JSON-RPC

One-shot request/response — POST JSON-RPC to `/` without `text/event-stream` in `Accept` and read the response from the same HTTP response body. Useful for quick `curl` testing and for MCP clients that only speak plain HTTP.

```bash
curl -s http://localhost:3000/health
# {"status":"ok","service":"dnSpy MCP Server"}

curl -s -X POST http://localhost:3000/ \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
```

### Server-Sent Events (MCP 2024-11-05)

Legacy two-endpoint transport kept for backwards compatibility with MCP Inspector and older clients: a long-lived SSE stream, plus a POST endpoint for client messages.

1. `GET /sse` — opens `text/event-stream`. The first event (`event: endpoint`) carries the URL the client should POST to (`/message?sessionId=<id>`).
2. `POST /message?sessionId=<id>` — accepts a JSON-RPC request, returns `202 Accepted`, and writes the real JSON-RPC response onto the corresponding SSE stream as an `event: message`.

```bash
# Terminal A: open the stream and keep it open
curl -N http://localhost:3000/sse
# event: endpoint
# data: /message?sessionId=<sessionId>
# ... (later, once POST arrives) ...
# event: message
# data: {"jsonrpc":"2.0","id":1,"result":...}

# Terminal B: send a request on that session
curl -X POST "http://localhost:3000/message?sessionId=<sessionId>" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
# HTTP 202 Accepted — the response appears on Terminal A's SSE stream
```

### Client configuration

#### Claude Code

Use the CLI to register the server once — it picks up the Streamable HTTP transport at `/`:

```bash
claude mcp add --transport http dnspy http://localhost:3000
# verify:
claude mcp list
```

Or add it to a checked-in `.mcp.json` at your project root (scoped to the project):

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "http",
      "url": "http://localhost:3000"
    }
  }
}
```

Run `/mcp` inside Claude Code to confirm `dnspy` is connected and list its tools.

#### Claude Desktop

```json
{
  "mcpServers": {
    "dnspy": {
      "command": "http",
      "args": ["http://localhost:3000"]
    }
  }
}
```

#### codex

See the Streamable HTTP section above for the `~/.codex/config.toml` snippet.

## Development

```bash
# Single-TFM builds for fast iteration
dotnet build -c Debug -f net48
dotnet build -c Debug -f net10.0-windows
```

### Project layout

```
dnSpy.Extension.MCP/
├── .github/workflows/      GitHub Actions (build, release)
├── McpServer.cs            HttpListener HTTP + SSE + Streamable HTTP + port fallback
├── McpProtocol.cs          JSON-RPC 2.0 / MCP DTOs
├── McpTools.cs             Analysis tools + MEF export + dispatch (sealed partial)
├── McpTools.IL.cs          IL view/patch/revert/save + operand renderer & parser
├── McpSettings.cs          Settings view-model + persistence + log (disk log in Debug only)
├── McpSettingsPage.cs      IAppSettingsPageProvider for dnSpy settings dialog
├── BepInExResources.cs     Embedded BepInEx docs (6 resources)
├── TheExtension.cs         IExtension entry point; starts server on Loaded
├── tests/fixtures/         TestIL.cs + build-fixture.ps1 + run-tests.ps1 (E2E harness)
└── dnSpy.Extension.MCP.csproj
```

### Architecture notes

- **Targets**: `net48` and `net10.0-windows` (inherited from `DnSpyCommon.props`).
- **Transport**: a single `HttpListener` serves the plain HTTP JSON-RPC, 2024-11-05 SSE, and 2025-03-26 Streamable HTTP paths on one port. Kestrel is intentionally **not** used — dnSpy's self-contained .NET bundle does not ship ASP.NET Core, so any `Microsoft.AspNetCore.*` reference would cause a silent `TypeLoadException` during MEF composition and the extension's `IExtension` part would never instantiate.
- **MEF**: services use `[Export(typeof(T))]` + `[ImportingConstructor]`. Don't `new` up `McpServer` / `McpSettings` / `McpTools`.
- **UI-thread marshalling**: every tool handler in `ExecuteTool` runs on the WPF dispatcher. `IDocumentTreeView` nodes are `DispatcherObject`s and throw "calling thread cannot access this object" if read from an HTTP worker, so marshalling is mandatory; handlers that already take the dispatcher path (patch, revert, save) double-wrap harmlessly.
- **Error codes**: `ArgumentException` inside a tool handler → JSON-RPC `-32602` (invalid params); any other exception → `-32603` (internal error).
- **Logging**: `McpSettings.Log(...)` writes to the in-UI log pane always, and to `D:\dnspy-mcp.log` only in **Debug** builds. Release builds keep everything in-memory; no writable `D:` drive is required on end-user machines.

## Protocol

Implements [MCP](https://modelcontextprotocol.io/) version `2024-11-05` over JSON-RPC 2.0.

Supported methods: `initialize`, `ping`, `tools/list`, `tools/call`, `resources/list`, `resources/read`, and `notifications/*`.

## CI / Release

- `.github/workflows/build.yml` — builds both TFMs on every push/PR.
- `.github/workflows/release.yml` — builds release DLLs and attaches them to the GitHub release on tag push (`v*.*.*`).

```bash
git tag v1.0.0
git push origin v1.0.0
```

## Technical details

- **Dependencies**: `dnSpy.Contracts.DnSpy`, `dnSpy.Contracts.Logic`, `dnlib`, `System.Text.Json` (package on `net48`, in-box on `net10.0-windows`).
- **BFS path finding**: `find_path_to_type` does breadth-first search over each type's fields and properties.
- **Decompilation**: uses dnSpy's default decompiler (usually C#) via `IDecompilerService`.
- **IL writing**: `save_assembly` calls `((ModuleDefMD)module).NativeWrite(path, NativeModuleWriterOptions)` for modules loaded from disk (preserves native stubs, Win32 resources, delay-loaded imports, mixed-mode code) and `module.Write(path, ModuleWriterOptions)` for freshly constructed modules. Memory-mapped I/O is disabled via `peImage as dnlib.PE.IInternalPEImage` before the write — the internal `IMmapDisabler` in `dnSpy.AsmEditor` is inlined to avoid depending on AsmEditor.
- **Cross-method references** in `patch_method_il` operands (`method:`, `field:`, `type:`) are resolved by walking every loaded module for a `FullName` match and then imported into the destination module via `new Importer(module, ImporterOptions.TryToUseDefs)`.

## Troubleshooting

### Settings page shows but the server never starts

Most commonly a MEF composition failure for the `IExtension` part while `IAppSettingsPageProvider` (the settings page) composes fine. Symptoms: the MCP Server page exists and lets you toggle Enable Server, but nothing happens on click and no log ever appears. Root cause is usually a missing runtime dependency — check the on-disk fallback log first, and make sure you deployed the DLL matching your dnSpy TFM.

### Port already in use

The server automatically falls back to `port + 1` (up to 20 tries). Look for `Port N is in use; falling back to M` in the log — clients should connect to the fallback port.

### Build errors

- Ensure you cloned dnSpyEx with `--recursive` (submodules must be initialized).
- Run `dotnet restore` in the dnSpyEx repo root.
- Requires .NET 10 SDK (previous dnSpy versions used .NET 8; `DnSpyCommon.props` is the source of truth).

## License

Same as dnSpyEx — see the [dnSpyEx repository](https://github.com/dnSpyEx/dnSpy).

## Acknowledgments

- [dnSpyEx](https://github.com/dnSpyEx/dnSpy) — .NET debugger and assembly editor
- [Model Context Protocol](https://modelcontextprotocol.io/) — Anthropic's MCP specification
- [BepInEx](https://github.com/BepInEx/BepInEx) — Unity game modding framework
