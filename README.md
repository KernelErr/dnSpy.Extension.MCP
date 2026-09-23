# dnSpy MCP Extension

Lets AI assistants such as Claude work with .NET assemblies through [dnSpyEx](https://github.com/dnSpyEx/dnSpy) over the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/): decompile, search, follow cross-references, patch IL, rename symbols and generate BepInEx / Harmony code — 32 tools in all.

Chinese / 中文说明: see [README.zh-CN.md](README.zh-CN.md).

## Two ways to run it

| | Headless | Inside dnSpy |
|---|---|---|
| What runs | `dnSpy.Extension.MCP.Headless.exe`, no window | The extension, inside the dnSpy window |
| How it starts | Your MCP client launches it when needed and stops it afterwards | You open dnSpy and turn on **Enable Server** |
| Transport | stdio | HTTP, `http://localhost:3000` |
| Assemblies | Its own: the AI opens them with `open_files` | Whatever is open in dnSpy (the AI can open more) |
| Patches and renames | In memory until `save_assembly` writes them | The same, and shown live in dnSpy's tree and tabs |
| Where to get it | The all-in-one zips | The all-in-one zips, or the plugin-only DLL |

Both serve the same tools and resources, and the end-to-end test suite runs against each. Use headless when you only want the AI to do the work; use dnSpy when you also want to look at the assemblies yourself.

## Quick start

1. **Download** the all-in-one zip for your system from [Releases](https://github.com/KernelErr/dnSpy.Extension.MCP/releases) ([which one?](#installation)) and unzip it, e.g. to `C:\Tools\dnSpy`.
2. **Connect Claude.** In Claude Code, either
   - register the headless host — Claude Code starts it when needed:
     ```bash
     claude mcp add --scope user dnspy -- "C:\Tools\dnSpy\dnSpy.Extension.MCP.Headless.exe"
     ```
   - or run `dnSpy.exe`, tick **View → Options → MCP Server → Enable Server**, and register its URL:
     ```bash
     claude mcp add --scope user --transport http dnspy http://localhost:3000
     ```

   For Claude Desktop and other clients, see [Connecting a client](#connecting-a-client).
3. **Ask**, in plain language:
   > *"Open `C:\Games\MyGame\MyGame_Data\Managed\Assembly-CSharp.dll`, find every method that uses the string `SAVEFILE`, and show me the decompiled `SaveGame` method."*

   Claude picks the tools itself (`open_files` → `search_string_literals` → `decompile_method`).

## Installation

### All-in-one zip (recommended)

Each zip on [Releases](https://github.com/KernelErr/dnSpy.Extension.MCP/releases) is a complete dnSpy with the extension already installed and the headless host next to `dnSpy.exe`. Unzip it anywhere; there is nothing else to set up.

| File | dnSpy build | Needs |
|------|-------------|-------|
| `dnSpy-MCP-win-x64.zip` | .NET 10, self-contained, x64 | Nothing — the runtime is included |
| `dnSpy-MCP-win-x86.zip` | .NET 10, self-contained, x86 | Nothing — the runtime is included |
| `dnSpy-MCP-net48.zip` | .NET Framework 4.8 | .NET Framework 4.8 (part of Windows 10 and later) |

### Plugin-only (dnSpy already installed)

This adds the in-dnSpy server to a dnSpy you already have. The headless host is only in the zips, which set up the runtime it needs.

1. Download `dnSpy.Extension.MCP-net10.0-windows.dll` for dnSpy on .NET, or `dnSpy.Extension.MCP-net48.dll` for dnSpy on .NET Framework.
2. Rename it to `dnSpy.Extension.MCP.x.dll` and put it in a folder of the same name under `bin\Extensions`:
   ```
   C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll
   ```
3. Restart dnSpy.

dnSpy silently skips the DLL without the `.x` suffix or outside its own folder; if **View → Options** has no **MCP Server** page, check the path.

To build from source, see [Development](#development).

## Connecting a client

The examples assume the zip is unzipped to `C:\Tools\dnSpy` and dnSpy's server uses the default port 3000.

### Claude Code

```bash
# Headless
claude mcp add --scope user dnspy -- "C:\Tools\dnSpy\dnSpy.Extension.MCP.Headless.exe"

# Inside dnSpy (with Enable Server ticked)
claude mcp add --scope user --transport http dnspy http://localhost:3000
```

`--scope user` makes `dnspy` available in all your projects; without it, only in the current one. `claude mcp list` should then report `✔ Connected` (for the headless host it starts one to check), and `/mcp` inside Claude Code lists its tools.

To share the setup through a repository, put a `.mcp.json` at its root (`claude mcp add --scope project …` writes one); Claude Code asks each user to approve it once:

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "stdio",
      "command": "C:\\Tools\\dnSpy\\dnSpy.Extension.MCP.Headless.exe",
      "args": []
    }
  }
}
```

For dnSpy's server, use `"dnspy": { "type": "http", "url": "http://localhost:3000" }`.

### Claude Desktop

Claude Desktop starts local MCP servers itself, over stdio — which is what the headless host is:

1. Open **Settings → Developer → Edit Config**. This opens `%APPDATA%\Claude\claude_desktop_config.json`.
2. Add `dnspy` to `mcpServers` (backslashes doubled, since it's JSON):
   ```json
   {
     "mcpServers": {
       "dnspy": {
         "command": "C:\\Tools\\dnSpy\\dnSpy.Extension.MCP.Headless.exe",
         "args": []
       }
     }
   }
   ```
3. Quit Claude Desktop completely (tray icon → Quit) and start it again. **Settings → Developer** now shows `dnspy` as running, and its tools are available in your chats.

If it doesn't start, the host's log is in `%APPDATA%\Claude\logs\mcp-server-dnspy.log`.

### Other clients

Clients that launch stdio servers (Cursor, Chatbox, Cline, …) take the same `command` + `args` entry as Claude Desktop. For dnSpy's server, give them the URL `http://localhost:3000` (Streamable HTTP), or `http://localhost:3000/sse` if a client only speaks the older SSE transport.

codex:

```bash
codex mcp add dnspy -- "C:\Tools\dnSpy\dnSpy.Extension.MCP.Headless.exe"   # headless
codex mcp add dnspy --url http://localhost:3000                             # inside dnSpy
```

## Headless host

`dnSpy.Extension.MCP.Headless.exe` needs no arguments. It starts with nothing loaded, and the AI opens what it needs with `open_files` — a file, or a folder to load all its `*.dll` — just as File → Open would in dnSpy, so tell it which game or assembly to look at.

Optional switches: `--dnspy <folder>` (use another dnSpy installation), `--quiet` (no log), `--version`, `--help`. Paths given as arguments are loaded at startup.

Compared with the server inside dnSpy:

- **Separate process.** Each client that starts it gets its own instance, which doesn't see what's open in a dnSpy window.
- **Target files stay unlocked.** Assemblies are read into memory, so you can rebuild or replace them while it runs.
- **Default decompiler settings**, not the options set in dnSpy's GUI.
- **Logs to stderr**, which clients show in their MCP logs; stdout carries the protocol.

## Inside dnSpy

Settings are under **View → Options → MCP Server** and apply immediately:

- **Enable Server** — starts or stops the server.
- **Port** — default `3000`. If it's taken, the server tries the next ones (20 ports in all); the **Server Log** pane shows the port it bound to.
- **Host** — default `localhost`, which answers on `localhost`, `127.0.0.1` and `[::1]`.

A browser on `http://localhost:3000/` shows a status page; `http://localhost:3000/health` answers `{"status":"ok",…}`.

## Features

### Tools

**Loading**

- `open_files` — load assemblies from files or folders (e.g. a Unity game's `Managed` folder, optionally recursive). Only metadata is read; nothing is executed.

**Analysis and navigation**

- `list_assemblies`, `get_assembly_info` — what's loaded (with file paths), and one assembly's namespaces.
- `list_types`, `search_types` — types by namespace or by name (substring or `*` wildcard), including nested and compiler-generated ones; `base_type` finds subclasses, e.g. of `MonoBehaviour`.
- `get_type_info`, `list_methods`, `get_type_fields`, `get_type_property` — a type's members, with their metadata tokens.
- `search_members` — methods, fields, properties and events by name across all assemblies (dnSpy's Ctrl+Shift+K).
- `find_path_to_type` — how one type reaches another through fields and properties.
- `decompile_method`, `decompile_type`, `decompile_by_token` — C# for a method or a whole type, by name or by token. Async and iterator state machines are included even when the decompiler can't fold them back into `await` / `yield`.

**Cross-references**

- `find_callers`, `find_callees` — who calls a method, and what a method calls, reads and writes.
- `find_references` — every use of a method, field, type or string.
- `find_overrides` — overrides and interface implementations, in either direction (dnSpy's Analyze).
- `find_unity_messages` — Unity entry points (`Awake`, `Update`, `OnTriggerEnter`, …), which have no call sites to find.
- `find_by_attribute` — types and members carrying an attribute, e.g. `[SerializeField]` or `[BepInPlugin]`.

**Strings and constants**

- `search_string_literals`, `list_string_constants` — which methods use a string; every string in a type or method.
- `search_constants` — where a number is used: magic values, item IDs, thresholds.

**Editing**

- `get_method_il`, `patch_method_il`, `revert_method_il` — read a method's IL, edit it (replace / insert / delete), and undo.
- `force_return`, `nop_method` — make a method return a fixed value, or do nothing, without writing IL.
- `rename_symbol_by_token` — rename a type, member, parameter or generic parameter, or all of an enum's members at once, mapped by value.
- `save_assembly` — write the module to disk, backing up the original first.

**Code generation**

- `generate_harmony_patch`, `generate_bepinex_plugin` — a compile-ready HarmonyX patch, or a whole BepInEx plugin, built from the target methods' real signatures.

Every `assembly_name` takes the simple name, the full name or the file path; pass the path when two loaded assemblies share a name. Tokens can be decimal or `0x` hex, as dnSpy shows them. Long lists come back in pages.

### Resources

Six BepInEx guides ship inside the extension, served through `resources/list` / `resources/read`: plugin structure, Harmony patching, configuration, common scenarios, IL2CPP, and Mono vs. IL2CPP.

## Editing IL

`get_method_il` and `patch_method_il` write each operand as one tagged string, the same in both directions:

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

`calli` / `InlineSig` is not supported.

Asked to *"make `AddOne` add 41 instead of 1 and save it"*, the AI reads the IL, finds `{"index":1,"opcode":"ldc.i4.1"}`, sends `patch_method_il` the edit `{"op":"replace","index":1,"opcode":"ldc.i4","operand":"int:41"}`, and calls `save_assembly`.

- **Undo with `revert_method_il`**, not Ctrl+Z: edits don't go through dnSpy's undo stack. The original body is kept even after saving, so you can revert and save again.
- **Saving over the original** first copies it to `<file>.<yyyyMMdd-HHmmss>.bak`. GAC assemblies are refused.
- **dnSpy doesn't reload saved files.** Reopen the assembly to see what's on disk.
- **Instructions only.** Locals and exception handlers are shown, but can't be added or removed.

## Troubleshooting

**The client can't start the headless host.** Run it in a terminal with `--version`. It prints a version when the installation is complete, or `couldn't find dnSpy` when it isn't next to `dnSpy.Console.exe` — keep it in the unzipped folder. Then check the client's MCP log: Claude Desktop writes `%APPDATA%\Claude\logs\mcp-server-dnspy.log`; in Claude Code, see `claude mcp list` or run `claude --debug`.

**Claude Code shows the server as "Pending approval".** It comes from a project's `.mcp.json`: start `claude` in that project and approve it.

**The MCP Server settings page is missing.** dnSpy didn't find the DLL; see [Plugin-only](#plugin-only-dnspy-already-installed) for where it goes.

**The settings page is there, but the server never starts.** The extension failed to load, usually because the DLL doesn't match dnSpy's runtime (the `net48` DLL in a .NET dnSpy, or the reverse). Debug builds also log to `D:\dnspy-mcp.log`.

**The port is in use.** The server moves on to the next free port and logs `Port N is in use; falling back to M`; point the client at that port.

## Protocol and transports

Both hosts speak MCP over JSON-RPC 2.0. `initialize` answers with the client's protocol version when it's `2025-06-18`, `2025-03-26` or `2024-11-05`, and with `2025-06-18` otherwise. Supported methods: `initialize`, `ping`, `tools/list`, `tools/call`, `resources/list`, `resources/read`, `resources/templates/list` (always empty) and `notifications/*`; anything else gets `-32601`. A tool that fails returns a result with `isError: true` and the reason, so the AI can correct itself.

The headless host uses stdio: one JSON-RPC message per line on stdin and stdout. Inside dnSpy, one port serves three HTTP transports:

| Transport | Endpoints | Used by |
|-----------|-----------|---------|
| Streamable HTTP (2025-03-26) | `POST` / `GET` / `DELETE` on `/` or `/mcp`, session in `Mcp-Session-Id` | Claude Code, codex, most current clients |
| SSE (2024-11-05) | `GET /sse`, then `POST /message?sessionId=<id>` | MCP Inspector, older clients |
| Plain JSON-RPC | `POST /` without `text/event-stream` in `Accept` | `curl`, scripts |

Sessions live in dnSpy's memory. A client that keeps its session ID across a dnSpy restart is accepted with it rather than refused, so it doesn't have to reconnect (clients built on the official TypeScript SDK never re-initialize by themselves); only a session ended with `DELETE` gets `404`.

```bash
curl -s http://localhost:3000/health
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

## Development

The extension builds inside a [dnSpyEx](https://github.com/dnSpyEx/dnSpy) checkout, at the tag CI pins (`DNSPY_REF` in the workflows, currently `v6.6.0`):

```bash
git clone --recursive --branch v6.6.0 https://github.com/dnSpyEx/dnSpy.git
cd dnSpy
git clone https://github.com/KernelErr/dnSpy.Extension.MCP.git Extensions/dnSpy.Extension.MCP
cd Extensions/dnSpy.Extension.MCP

dotnet build -c Release                     # the extension, net48 + net10.0-windows
dotnet build headless -c Release            # the headless host (builds the extension too)
dotnet build -c Debug -f net10.0-windows    # one TFM, for quick iteration
```

To try a build in dnSpy, copy `bin/<Config>/<TFM>/dnSpy.Extension.MCP.x.dll` into `<dnSpy>\bin\Extensions\dnSpy.Extension.MCP\`.

**Changes must pass the end-to-end suite in both modes.** It runs against the dnSpy built in the parent checkout, so build that first — `./build.ps1 -buildtfm net` from the dnSpy root (`-buildtfm netframework` for the net48 runs; add `-NoMsbuild` if you only have the .NET SDK):

```powershell
pwsh tests/fixtures/run-tests.ps1                  # inside dnSpy, over HTTP
pwsh tests/fixtures/run-tests.ps1 -Headless        # the headless host, over stdio
pwsh tests/fixtures/run-tests.ps1 -Tfm net48       # either mode, against the .NET Framework build
```

The suite builds a small fixture assembly (`tests/fixtures/TestIL.cs`), loads it and drives every tool against it, down to patching, saving and running the saved DLL. CI doesn't run it, so run both modes before you merge.

Architecture, conventions and the reasons behind them are in [CLAUDE.md](CLAUDE.md).

### Project layout

```
dnSpy.Extension.MCP/
├── .github/workflows/          build.yml (every push / PR), release.yml (published releases)
├── McpServer.cs                HTTP server: plain JSON-RPC, SSE, Streamable HTTP, port fallback
├── McpDispatcher.cs            JSON-RPC dispatch shared by both hosts
├── McpProtocol.cs              JSON-RPC / MCP types
├── IMcpHost.cs                 What the tools need from their host
├── DnSpyMcpHost.cs             That host inside dnSpy: documents, decompiler, tree/tab refresh
├── McpTools*.cs                The 32 tools (analysis, IL, strings, xref, rename)
├── McpSettings*                Settings page, persistence and log
├── BepInExResources.cs         The six embedded guides
├── TheExtension.cs             Entry point: starts the server when dnSpy loads
├── headless/                   The headless host and deploy-headless.ps1
├── tests/check-host-deps.ps1   net48 dependency-version guard (run by CI)
└── tests/fixtures/             Fixture assembly and the end-to-end suite
```

## CI and releases

- `build.yml` runs on every push and pull request: the net48 dependency check (`tests/check-host-deps.ps1`), then Debug and Release builds of the extension and the headless host.
- `release.yml` runs when a GitHub Release is **published** — pushing a tag alone doesn't start it. It builds dnSpy at the pinned tag, stamps the release version into the extension, deploys the extension and the headless host into each bundle, and attaches the zips and DLLs to the release.

```bash
git tag v0.1.15 && git push origin v0.1.15
gh release create v0.1.15 --title v0.1.15 --notes "..."   # publishing starts release.yml
```

## License

Same as dnSpyEx — see the [dnSpyEx repository](https://github.com/dnSpyEx/dnSpy).

## Acknowledgments

- [dnSpyEx](https://github.com/dnSpyEx/dnSpy) — .NET debugger and assembly editor
- [Model Context Protocol](https://modelcontextprotocol.io/) — Anthropic's MCP specification
- [BepInEx](https://github.com/BepInEx/BepInEx) — Unity game modding framework
