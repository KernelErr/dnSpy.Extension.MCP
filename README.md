# dnSpy MCP Extension

[![Build](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml)
[![Release](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml)

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) extension for [dnSpyEx](https://github.com/dnSpyEx/dnSpy) that exposes .NET assembly analysis tools to AI assistants like Claude.

Chinese / 中文说明: see [README.zh-CN.md](README.zh-CN.md).

## Features

### MCP Tools (10 total)

1. **list_assemblies** — list all loaded assemblies with metadata
2. **get_assembly_info** — detailed info about a specific assembly (paginated namespaces)
3. **list_types** — all types in an assembly or namespace (paginated)
4. **get_type_info** — fields, properties, and paginated methods for a type
5. **get_type_fields** — filter fields by wildcard pattern (e.g. `*Bonus*`)
6. **get_type_property** — detailed info about a property including getter/setter
7. **find_path_to_type** — BFS over fields/properties to connect two types
8. **decompile_method** — decompile a method to C#
9. **search_types** — wildcard / substring type search across all assemblies
10. **generate_bepinex_plugin** — BepInEx plugin template with Harmony hooks

### MCP Resources (6 total)

Embedded BepInEx documentation served over `resources/list` / `resources/read`:

1. **plugin-structure**
2. **harmony-patching** (Prefix / Postfix / Transpiler)
3. **configuration**
4. **common-scenarios**
5. **il2cpp-guide**
6. **mono-vs-il2cpp**

All docs ship inside the DLL — no network required.

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
- **Port** — preferred TCP port (default `3000`). If the port is already in use, the server automatically tries `port + 1`, up to 20 attempts, and logs which port it actually bound to. Check the Server Log pane (or `%TEMP%\…` fallback log) for the resolved port.
- **Host** — bind address (default `localhost`).

## Transports

Both transports run on the same `HttpListener` on the same port.

### Plain HTTP JSON-RPC

One-shot request/response — POST JSON-RPC to `/` and read the response from the same HTTP response body.

```bash
curl -s http://localhost:3000/health
# {"status":"ok","service":"dnSpy MCP Server"}

curl -s -X POST http://localhost:3000/ \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
```

### Server-Sent Events (MCP 2024-11-05)

Two-endpoint transport: a long-lived SSE stream, plus a POST endpoint for client messages.

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

### Claude Desktop configuration

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
├── McpServer.cs            HttpListener HTTP + SSE server + port fallback
├── McpProtocol.cs          JSON-RPC 2.0 / MCP DTOs
├── McpTools.cs             10 MCP tools (wraps dnSpy services)
├── McpSettings.cs          Settings view-model + persistence + file log
├── McpSettingsPage.cs      IAppSettingsPageProvider for dnSpy settings dialog
├── BepInExResources.cs     Embedded BepInEx docs (6 resources)
├── TheExtension.cs         IExtension entry point; starts server on Loaded
└── dnSpy.Extension.MCP.csproj
```

### Architecture notes

- **Targets**: `net48` and `net10.0-windows` (inherited from `DnSpyCommon.props`).
- **Transport**: a single `HttpListener` serves both the plain HTTP JSON-RPC path and the SSE path. Kestrel is intentionally **not** used — dnSpy's self-contained .NET bundle does not ship ASP.NET Core, so any `Microsoft.AspNetCore.*` reference would cause a silent `TypeLoadException` during MEF composition and the extension's `IExtension` part would never instantiate.
- **MEF**: services use `[Export(typeof(T))]` + `[ImportingConstructor]`. Don't `new` up `McpServer` / `McpSettings` / `McpTools`.
- **Error codes**: `ArgumentException` inside a tool handler → JSON-RPC `-32602` (invalid params); any other exception → `-32603` (internal error).
- **Logging**: `McpSettings.Log(...)` writes to both the in-UI log pane and an on-disk fallback file. The on-disk log is authoritative — it survives when the dispatcher isn't yet running or when the Settings dialog is closed.

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
