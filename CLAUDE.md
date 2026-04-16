# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A dnSpyEx extension that embeds an HTTP/SSE MCP server inside dnSpy, exposing .NET
assembly analysis tools (assembly/type inspection, decompilation, BFS path-finding,
BepInEx plugin scaffolding) to AI assistants. The DLL is loaded by dnSpy at startup;
it is not a standalone program.

## Build

The project is not buildable standalone — it expects to live inside a checked-out
dnSpyEx tree at `dnSpy/Extensions/dnSpy.Extension.MCP/` because
`dnSpy.Extension.MCP.csproj` imports `..\..\DnSpyCommon.props` and references
`..\..\dnSpy\dnSpy.Contracts.*\*.csproj`.

To set up a build tree from scratch:

```bash
git clone --recursive https://github.com/dnSpyEx/dnSpy.git
git clone https://github.com/KernelErr/dnSpy.Extension.MCP.git \
    dnSpy/Extensions/dnSpy.Extension.MCP
cd dnSpy/Extensions/dnSpy.Extension.MCP
dotnet build -c Release                         # both TFMs
dotnet build -c Debug -f net48                  # single-TFM, fast iteration
dotnet build -c Debug -f net10.0-windows
```

Requires the .NET 10 SDK — `DnSpyCommon.props` in the parent dnSpy repo is the
source of truth for target frameworks and package versions. The assembly name is
`dnSpy.Extension.MCP.x` (the `.x` suffix is required by dnSpy's extension loader;
don't remove it).

No test suite exists. CI (`.github/workflows/build.yml`) checks out dnSpyEx,
moves this repo under `Extensions/`, and runs the same `dotnet build` commands
above for net48 and net10.0-windows on both Debug and Release.

## Architecture — non-obvious constraints

**Do not reference `Microsoft.AspNetCore.*` / Kestrel.** dnSpy's self-contained
.NET bundle does not include ASP.NET Core. Adding a Kestrel reference compiles
fine but causes a silent `TypeLoadException` during MEF composition at runtime —
the `IExtension` part is silently skipped and the settings page may still appear
while the server never starts. `System.Net.HttpListener` is used on both TFMs
for this reason; see the comment in `dnSpy.Extension.MCP.csproj`.

**MEF composition is the wiring.** All services (`McpServer`, `McpSettings`,
`McpTools`, `BepInExResources`, `TheExtension`) are composed via
`[Export(typeof(T))]` + `[ImportingConstructor]`. Never `new` them up. The entry
point is `TheExtension.OnEvent(ExtensionEvent.Loaded)` which calls
`mcpServer.Start()`; `AppExit` stops it. `McpSettingsImpl.SetServer(mcpServer)`
is what lets toggling "Enable Server" in the UI restart the listener.

**Single `HttpListener`, two transports on one port.** `McpServer` serves both
plain HTTP JSON-RPC (POST `/`) and MCP 2024-11-05 SSE (`GET /sse` +
`POST /message?sessionId=<id>`). SSE responses are written to the long-lived
stream keyed by `sessionId` in `sseSessions`, while the POST returns 202.

**Port fallback.** `FindAvailablePort` probes `startPort..startPort+19` and
returns the first free one. There is an intentional TOCTOU race (the probe
`TcpListener` is stopped before the real `HttpListener` binds). `ActualPort`
exposes the bound port to clients, and the fallback is logged. Clients must
read the log to know which port was bound.

**Error-code contract in tool handlers.** In `McpTools`, throw `ArgumentException`
for bad inputs — the dispatcher maps it to JSON-RPC `-32602` (invalid params).
Any other exception maps to `-32603` (internal error). Don't catch-and-return
error strings from tools; let them throw.

**Logging.** `McpSettings.Log(...)` writes to both the in-UI `ObservableCollection`
pane and an on-disk fallback file under `%TEMP%`. The on-disk log is
authoritative — it survives before the WPF dispatcher is running and when the
Settings dialog is closed, which is exactly when MEF composition failures
happen. When diagnosing "server didn't start," read the on-disk log first.

**Deployment layout.** The built DLL must be deployed to
`<dnSpy-Install>\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll`
— folder name matches the DLL stem, `.x.dll` suffix present, one level deep
under `Extensions\`. If the DLL ends up directly under `Extensions\` or without
`.x`, dnSpy silently skips it.

## File map (one-liners)

- `TheExtension.cs` — `IExtension` entry point; starts/stops server on lifecycle.
- `McpServer.cs` — HttpListener, HTTP + SSE dispatch, port fallback, JSON-RPC.
- `McpProtocol.cs` — JSON-RPC 2.0 / MCP DTOs.
- `McpTools.cs` — all 10 MCP tools; wraps `IDocumentTreeView` + `IDecompilerService`.
- `BepInExResources.cs` — 6 embedded BepInEx docs served via `resources/*`.
- `McpSettings.cs` / `McpSettingsImpl` — view-model, persistence, logging.
- `McpSettingsPage.cs` + `McpSettingsControl.xaml[.cs]` — dnSpy settings UI.

## Release

Tag-triggered — `.github/workflows/release.yml` builds release DLLs and attaches
them to the GitHub release on `v*.*.*` tag push:

```bash
git tag v1.0.0 && git push origin v1.0.0
```
