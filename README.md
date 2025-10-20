# dnSpy MCP Extension

[![Build](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml)
[![Release](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml)

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) extension for [dnSpyEx](https://github.com/dnSpyEx/dnSpy) that exposes .NET assembly analysis tools to AI assistants like Claude.

## Features

### MCP Tools (10 total)

The extension provides 10 powerful tools for analyzing .NET assemblies:

1. **list_assemblies** - List all loaded assemblies with metadata
2. **get_assembly_info** - Get detailed information about a specific assembly
3. **list_types** - List all types in an assembly with filtering options
4. **get_type_info** - Get comprehensive type information (fields, properties, methods, events)
5. **get_type_fields** - Filter fields by wildcard pattern (e.g., `*Bonus*`)
6. **get_type_property** - Get detailed property information including getters/setters
7. **find_path_to_type** - Find property/field chains connecting two types (BFS algorithm)
8. **get_method_signature** - Get detailed method signature information
9. **decompile_method** - Decompile method to C# source code
10. **search_types** - Search for types by name pattern across all assemblies

### MCP Resources (6 total)

Comprehensive embedded BepInEx plugin development documentation:

1. **plugin-structure** - Basic plugin setup and metadata
2. **harmony-patching** - HarmonyX patching guide (Prefix/Postfix/Transpiler)
3. **configuration** - Configuration system usage
4. **common-scenarios** - 8 common plugin development patterns
5. **il2cpp-guide** - Complete IL2CPP development guide
6. **mono-vs-il2cpp** - Side-by-side comparison and migration guide

All documentation is **self-contained** within the extension DLL - no internet connection required.

## Installation

### From Release (Recommended)

1. Download the latest release from the [Releases](https://github.com/KernelErr/dnSpy.Extension.MCP/releases) page
2. Choose the appropriate DLL for your dnSpy version:
   - **dnSpy.Extension.MCP-net48.dll** - For dnSpy .NET Framework 4.8
   - **dnSpy.Extension.MCP-net8.0.dll** - For dnSpy .NET 8.0
3. Rename the file to `dnSpy.Extension.MCP.x.dll`
4. Copy to `<dnSpy-Install-Path>\bin\Extensions\dnSpy.Extension.MCP\`
5. Restart dnSpy

### From Source

```bash
# Clone dnSpyEx repository
git clone https://github.com/dnSpyEx/dnSpy.git
cd dnSpy

# Clone this extension into Extensions folder
cd Extensions
git clone https://github.com/KernelErr/dnSpy.Extension.MCP.git

# Build the extension
cd dnSpy.Extension.MCP
dotnet build -c Release

# Copy to dnSpy installation
copy bin\Release\net48\dnSpy.Extension.MCP.x.dll <dnSpy-Install-Path>\bin\Extensions\dnSpy.Extension.MCP\
```

## Configuration

Access settings in dnSpy: **Edit → Settings → MCP Server**

Available settings:
- **Enable Server** - Start/stop the MCP server
- **Port** - HTTP server port (default: 3000)
- **Host** - Bind address (default: localhost for .NET Framework)

## Usage

### With Claude Desktop

Add to your Claude Desktop configuration (`claude_desktop_config.json`):

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

### Testing the Server

Check server health:
```bash
curl http://localhost:3000/health
```

Expected response:
```json
{"status":"ok","service":"dnSpy MCP Server"}
```

## Development

### Building

```bash
# Build for .NET Framework 4.8
dotnet build -c Release -f net48

# Build for .NET 8.0
dotnet build -c Release -f net8.0-windows
```

### Project Structure

```
dnSpy.Extension.MCP/
├── .github/workflows/      # GitHub Actions (build, release)
├── McpServer.cs           # HTTP server (Kestrel/.NET, HttpListener/.NET Framework)
├── McpProtocol.cs         # JSON-RPC 2.0 protocol models
├── McpTools.cs            # 10 MCP tools implementation
├── McpSettings.cs         # Settings UI and persistence
├── BepInExResources.cs    # Embedded documentation (6 resources)
└── dnSpy.Extension.MCP.csproj
```

### Architecture

- **Multi-framework support**: Targets both `net48` and `net8.0-windows`
- **Server implementation**:
  - .NET 8.0: ASP.NET Core Kestrel (high performance)
  - .NET Framework: HttpListener (compatibility)
- **MEF integration**: Uses dnSpy's Managed Extensibility Framework
- **Protocol**: JSON-RPC 2.0 over HTTP
- **CORS enabled**: Allows cross-origin requests

## Protocol

The extension implements [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) specification version `2024-11-05`.

Supported methods:
- `initialize` - Server initialization
- `ping` - Keepalive
- `tools/list` - List available tools
- `tools/call` - Execute a tool
- `resources/list` - List documentation resources
- `resources/read` - Read documentation content
- `notifications/*` - Handle client notifications

## CI/CD

### GitHub Actions Workflows

1. **Build Workflow** (`.github/workflows/build.yml`)
   - Triggers: Push to master/main, Pull Requests
   - Builds both net48 and net8.0-windows
   - Runs on Debug and Release configurations
   - Uploads artifacts for Release builds

2. **Release Workflow** (`.github/workflows/release.yml`)
   - Triggers: Push tags (`v*.*.*`) or manual dispatch
   - Builds release DLLs
   - Creates GitHub release with assets
   - Auto-generates release notes

### Creating a Release

```bash
# Tag the release
git tag v1.0.0
git push origin v1.0.0

# GitHub Actions will automatically:
# 1. Build both net48 and net8.0 DLLs
# 2. Create a GitHub release
# 3. Upload DLLs and README as assets
```

## Contributing

Contributions are welcome! Please:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

GitHub Actions will automatically build and test your PR.

## Technical Details

### Dependencies

- **dnSpy.Contracts.DnSpy** - dnSpy API contracts
- **dnSpy.Contracts.Logic** - Core logic contracts
- **dnlib** - .NET metadata library
- **System.Text.Json** - JSON serialization
- **ASP.NET Core** (net8.0 only) - Kestrel HTTP server

### BFS Path Finding

The `find_path_to_type` tool uses breadth-first search to find navigation paths between types:

```csharp
// Example: Find path from PlayerController to HealthBonus
// Result: PlayerController -> PlayerState -> Bonuses -> HealthBonus
```

### Decompilation

Uses dnSpy's built-in decompiler with configurable options:
- Language: C#
- Indentation: Tabs
- Comment verbosity: Configurable

## License

This extension follows the same license as dnSpyEx. Please see the [dnSpyEx repository](https://github.com/dnSpyEx/dnSpy) for license information.

## Acknowledgments

- [dnSpyEx](https://github.com/dnSpyEx/dnSpy) - The .NET debugger and assembly editor (continuation of dnSpy)
- [Model Context Protocol](https://modelcontextprotocol.io/) - Anthropic's MCP specification
- [BepInEx](https://github.com/BepInEx/BepInEx) - Unity game modding framework

## Troubleshooting

### Extension not loading
- Check dnSpy console for error messages
- Verify DLL is in correct location
- Ensure file is named `dnSpy.Extension.MCP.x.dll`

### Server not starting
- Check port is not already in use
- Review dnSpy console logs
- Try changing port in settings

### Build errors
- Ensure dnSpy repository is cloned in parent directory
- Run `dotnet restore` in dnSpy root
- Check .NET SDK version (requires .NET 8.0 SDK)

## Support

For issues, questions, or feature requests, please open an issue on the [GitHub repository](https://github.com/KernelErr/dnSpy.Extension.MCP/issues).
