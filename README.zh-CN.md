# dnSpy MCP 扩展

让 Claude 等 AI 助手通过 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 借助 [dnSpyEx](https://github.com/dnSpyEx/dnSpy) 处理 .NET 程序集：反编译、搜索、追踪交叉引用、修改 IL、重命名符号、生成 BepInEx / Harmony 代码，共 32 个工具。

English: see [README.md](README.md).

## 两种运行方式

| | Headless | 在 dnSpy 内运行 |
|---|---|---|
| 运行的是什么 | `dnSpy.Extension.MCP.Headless.exe`，没有窗口 | 扩展本身，运行在 dnSpy 窗口里 |
| 如何启动 | MCP 客户端需要时自动启动，用完自动关闭 | 打开 dnSpy 并开启 **Enable Server** |
| 传输方式 | stdio | HTTP，`http://localhost:3000` |
| 程序集 | 独立加载：AI 用 `open_files` 打开 | dnSpy 里已打开的内容（AI 也可以再打开别的） |
| 补丁与重命名 | 保存在内存中，`save_assembly` 时写盘 | 同左，并实时显示在 dnSpy 的树和标签页里 |
| 获取方式 | 一体化压缩包 | 一体化压缩包，或单独的插件 DLL |

两种方式提供相同的工具和资源，端到端测试对两者都会完整运行。只想让 AI 干活就用 headless；想同时自己在 dnSpy 里查看程序集，就用 dnSpy 内的服务器。

## 快速开始

1. **下载**：从 [Releases](https://github.com/KernelErr/dnSpy.Extension.MCP/releases) 下载对应系统的一体化压缩包（[选哪个？](#安装)），解压到任意位置，例如 `C:\Tools\dnSpy`。
2. **连接 Claude**：在 Claude Code 里任选一种：
   - 注册 headless 宿主，Claude Code 会在需要时自动启动它：
     ```bash
     claude mcp add --scope user dnspy -- "C:\Tools\dnSpy\dnSpy.Extension.MCP.Headless.exe"
     ```
   - 或者运行 `dnSpy.exe`，勾选 **视图 → 选项 → MCP Server → Enable Server**，然后注册它的地址：
     ```bash
     claude mcp add --scope user --transport http dnspy http://localhost:3000
     ```

   Claude Desktop 和其他客户端见[连接客户端](#连接客户端)。
3. **提问**，直接用自然语言：
   > *"打开 `C:\Games\MyGame\MyGame_Data\Managed\Assembly-CSharp.dll`，找出所有用到字符串 `SAVEFILE` 的方法，再把反编译后的 `SaveGame` 方法给我看。"*

   Claude 会自己选择工具（`open_files` → `search_string_literals` → `decompile_method`）。

## 安装

### 一体化压缩包（推荐）

[Releases](https://github.com/KernelErr/dnSpy.Extension.MCP/releases) 上的每个压缩包都是完整的 dnSpy，扩展已经装好，`dnSpy.exe` 旁边还有 headless 宿主。解压到任意位置即可，不需要其他设置。

| 文件 | dnSpy 版本 | 运行要求 |
|------|-----------|----------|
| `dnSpy-MCP-win-x64.zip` | .NET 10 自包含 x64 | 无，运行时已内置 |
| `dnSpy-MCP-win-x86.zip` | .NET 10 自包含 x86 | 无，运行时已内置 |
| `dnSpy-MCP-net48.zip` | .NET Framework 4.8 | .NET Framework 4.8（Windows 10 及以上自带） |

### 仅插件安装

适用于已经装了 dnSpy、只想加上 dnSpy 内服务器的情况。headless 宿主只在压缩包里提供，压缩包会为它配好所需的运行时。

1. 下载 `dnSpy.Extension.MCP-net10.0-windows.dll`（.NET 版 dnSpy）或 `dnSpy.Extension.MCP-net48.dll`（.NET Framework 版 dnSpy）。
2. 重命名为 `dnSpy.Extension.MCP.x.dll`，放进 `bin\Extensions` 下同名的文件夹：
   ```
   C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll
   ```
3. 重启 dnSpy。

缺少 `.x` 后缀、或者没有放在自己的文件夹里，dnSpy 都会静默跳过这个 DLL；如果 **视图 → 选项** 里没有 **MCP Server** 页，请检查路径。

从源码构建见[开发](#开发)。

## 连接客户端

示例假设压缩包解压到 `C:\Tools\dnSpy`，dnSpy 内的服务器使用默认端口 3000。

### Claude Code

```bash
# Headless
claude mcp add --scope user dnspy -- "C:\Tools\dnSpy\dnSpy.Extension.MCP.Headless.exe"

# 在 dnSpy 内运行（已勾选 Enable Server）
claude mcp add --scope user --transport http dnspy http://localhost:3000
```

`--scope user` 让 `dnspy` 在你的所有项目里可用；不加则只对当前项目生效。之后 `claude mcp list` 应显示 `✔ Connected`（对 headless 宿主，它会启动一个实例来检查），在 Claude Code 里运行 `/mcp` 可以看到它的工具。

想随仓库共享配置，就在仓库根目录放一个 `.mcp.json`（`claude mcp add --scope project …` 会生成它）；每个用户第一次使用时需要在 Claude Code 里批准一次：

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

dnSpy 内的服务器则写成 `"dnspy": { "type": "http", "url": "http://localhost:3000" }`。

### Claude Desktop

Claude Desktop 通过 stdio 自己启动本地 MCP 服务器，这正是 headless 宿主的工作方式：

1. 打开 **Settings → Developer → Edit Config**，会打开 `%APPDATA%\Claude\claude_desktop_config.json`。
2. 在 `mcpServers` 里加上 `dnspy`（这是 JSON，反斜杠要写两个）：
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
3. 彻底退出 Claude Desktop（托盘图标 → Quit）后重新打开。**Settings → Developer** 里会显示 `dnspy` 正在运行，对话中即可使用它的工具。

如果启动失败，headless 宿主的日志在 `%APPDATA%\Claude\logs\mcp-server-dnspy.log`。

### 其他客户端

能启动 stdio 服务器的客户端（Cursor、Chatbox、Cline 等）使用与 Claude Desktop 相同的 `command` + `args` 配置。连接 dnSpy 内的服务器时填地址 `http://localhost:3000`（Streamable HTTP）；只支持旧版 SSE 传输的客户端填 `http://localhost:3000/sse`。

codex：

```bash
codex mcp add dnspy -- "C:\Tools\dnSpy\dnSpy.Extension.MCP.Headless.exe"   # headless
codex mcp add dnspy --url http://localhost:3000                             # 在 dnSpy 内运行
```

## Headless 宿主

`dnSpy.Extension.MCP.Headless.exe` 不需要任何参数。它启动时不加载任何程序集，AI 会用 `open_files` 打开需要的文件（单个文件，或一个文件夹下的全部 `*.dll`），相当于 dnSpy 里的 文件 → 打开，所以直接告诉 AI 要分析哪个游戏或程序集即可。

可选开关：`--dnspy <文件夹>`（使用另一个 dnSpy 安装）、`--quiet`（不输出日志）、`--version`、`--help`。作为参数传入的路径会在启动时加载。

与 dnSpy 内的服务器相比：

- **独立进程。** 每个启动它的客户端各有一个实例，看不到 dnSpy 窗口里打开的内容。
- **不锁定目标文件。** 程序集读入内存，运行期间可以重新编译或替换它们。
- **使用默认反编译设置**，不带你在 dnSpy 界面里设置的选项。
- **日志写到 stderr**，客户端会在 MCP 日志里显示；stdout 用于协议通信。

## 在 dnSpy 内运行

设置位于 **视图 → 选项 → MCP Server**，应用后立即生效：

- **Enable Server**：启动或停止服务器。
- **Port**：默认 `3000`。端口被占用时会依次尝试后面的端口（共 20 个），实际绑定的端口见 **Server Log** 面板。
- **Host**：默认 `localhost`，`localhost`、`127.0.0.1` 和 `[::1]` 都能访问。

用浏览器打开 `http://localhost:3000/` 会看到状态页；`http://localhost:3000/health` 返回 `{"status":"ok",…}`。

## 功能

### 工具

**加载**

- `open_files`：从文件或文件夹加载程序集（例如 Unity 游戏的 `Managed` 文件夹，可递归）。只读取元数据，不执行任何代码。

**分析与导航**

- `list_assemblies`、`get_assembly_info`：已加载的程序集（含文件路径），以及单个程序集的命名空间。
- `list_types`、`search_types`：按命名空间或名称（子串或 `*` 通配）查找类型，包括嵌套类型和编译器生成的类型；`base_type` 可查找子类，例如 `MonoBehaviour` 的子类。
- `get_type_info`、`list_methods`、`get_type_fields`、`get_type_property`：类型的成员及其元数据 Token。
- `search_members`：按名称在所有程序集中搜索方法、字段、属性和事件（相当于 dnSpy 的 Ctrl+Shift+K）。
- `find_path_to_type`：一个类型如何通过字段和属性到达另一个类型。
- `decompile_method`、`decompile_type`、`decompile_by_token`：按名称或 Token 把方法或整个类型反编译为 C#。即使反编译器无法把 async / iterator 状态机还原成 `await` / `yield`，状态机的代码也会一并给出。

**交叉引用**

- `find_callers`、`find_callees`：谁调用了某个方法，以及某个方法调用、读写了什么。
- `find_references`：某个方法、字段、类型或字符串的所有使用位置。
- `find_overrides`：双向查找重写和接口实现（对应 dnSpy 的 Analyze）。
- `find_unity_messages`：Unity 的入口方法（`Awake`、`Update`、`OnTriggerEnter` 等），这些方法没有调用点，靠交叉引用找不到。
- `find_by_attribute`：带有某个特性的类型和成员，例如 `[SerializeField]`、`[BepInPlugin]`。

**字符串与常量**

- `search_string_literals`、`list_string_constants`：哪些方法用到了某个字符串；某个类型或方法里的全部字符串。
- `search_constants`：某个数值在哪里被使用，例如魔法数、物品 ID、阈值。

**编辑**

- `get_method_il`、`patch_method_il`、`revert_method_il`：读取方法的 IL、编辑（替换 / 插入 / 删除）、撤销。
- `force_return`、`nop_method`：不用写 IL，让方法返回固定值，或什么都不做。
- `rename_symbol_by_token`：重命名类型、成员、参数或泛型参数，也可以按值一次性重命名枚举的全部成员。
- `save_assembly`：把模块写回磁盘，写之前先备份原文件。

**代码生成**

- `generate_harmony_patch`、`generate_bepinex_plugin`：根据目标方法的真实签名，生成可直接编译的 HarmonyX 补丁，或完整的 BepInEx 插件。

所有 `assembly_name` 参数都接受简单名、完整名或文件路径；两个已加载的程序集同名时请传路径。Token 可以是十进制，也可以是 dnSpy 界面里显示的 `0x` 十六进制。较长的列表会分页返回。

### 资源

扩展内置六份 BepInEx 指南，通过 `resources/list` / `resources/read` 提供：插件结构、Harmony 补丁、配置、常见场景、IL2CPP，以及 Mono 与 IL2CPP 的对比。

## 编辑 IL

`get_method_il` 与 `patch_method_il` 把每个操作数写成一个带标签的字符串，读和写使用同一套语法：

| 标签 | 示例 | 对应指令 |
|------|------|----------|
| `int:` / `int8:` / `uint8:` / `long:` | `int:42` | `ldc.i4`、`ldc.i4.s`、`ldc.i8` |
| `float:` / `double:` | `double:3.14` | `ldc.r4`、`ldc.r8` |
| `str:` *(JSON 字符串字面量)* | `str:"hello\n"` | `ldstr` |
| `method:` *(dnlib FullName)* | `method:System.Void Ns.T::M(System.Int32)` | `call`、`callvirt`、`newobj`、`ldftn`、`ldvirtftn`、`jmp` |
| `field:` | `field:System.Int32 Ns.T::F` | `ldfld`、`stfld`、`ldsfld`、`stsfld`、`ldflda`、`ldsflda` |
| `type:` | `type:System.String` | `castclass`、`isinst`、`box`、`unbox`、`newarr`、`initobj`、`ldelem*`、`stelem*` 等 |
| `token:method:…` / `token:field:…` / `token:type:…` | `token:type:System.String` | `ldtoken` |
| `label:<idx>` | `label:7` | `br`、`brtrue.s`、`blt` 等跳转 |
| `switch:[<i>,<i>,…]` | `switch:[3,7,12]` | `switch` |
| `local:<idx>` | `local:0` | `ldloc*`、`stloc*` |
| `arg:<idx>` | `arg:1` | `ldarg*`、`starg*` |
| *(空字符串)* | `""` | 无操作数（`ldarg.0`、`add`、`ret` 等） |

`calli` / `InlineSig` 暂不支持。

比如让 AI *"把 `AddOne` 里的加 1 改成加 41 并保存"*，它会先读取 IL，找到 `{"index":1,"opcode":"ldc.i4.1"}`，然后向 `patch_method_il` 发送编辑 `{"op":"replace","index":1,"opcode":"ldc.i4","operand":"int:41"}`，再调用 `save_assembly`。

- **用 `revert_method_il` 撤销**，而不是 Ctrl+Z：这些编辑不经过 dnSpy 的撤销栈。保存之后原方法体依然保留，可以撤销后再保存一次。
- **覆盖原文件保存时**会先复制一份 `<文件>.<yyyyMMdd-HHmmss>.bak`。GAC 中的程序集会被拒绝。
- **dnSpy 不会重新加载保存后的文件。** 想看磁盘上的内容，请重新打开该程序集。
- **只能改指令。** 局部变量和异常处理块可以查看，但不能增删。

## 故障排查

**客户端启动不了 headless 宿主。** 在终端里用 `--version` 运行它：安装完整时会输出版本号；如果它不在 `dnSpy.Console.exe` 旁边，会提示 `couldn't find dnSpy`，请把它留在解压出来的文件夹里。然后查看客户端的 MCP 日志：Claude Desktop 写在 `%APPDATA%\Claude\logs\mcp-server-dnspy.log`；Claude Code 可以看 `claude mcp list`，或用 `claude --debug` 启动。

**Claude Code 显示服务器 "Pending approval"。** 这是项目 `.mcp.json` 里配置的服务器：在该项目里启动 `claude` 并批准即可。

**设置里没有 MCP Server 页。** dnSpy 没找到这个 DLL，放置位置见[仅插件安装](#仅插件安装)。

**设置页在，但服务器始终不启动。** 扩展加载失败，通常是 DLL 与 dnSpy 的运行时不匹配（在 .NET 版 dnSpy 里放了 `net48` 的 DLL，或者反过来）。Debug 构建还会把日志写到 `D:\dnspy-mcp.log`。

**端口被占用。** 服务器会改用下一个空闲端口，并记录 `Port N is in use; falling back to M`；让客户端连接这个端口即可。

## 协议与传输

两种宿主都使用基于 JSON-RPC 2.0 的 MCP。`initialize` 时，如果客户端请求的协议版本是 `2025-06-18`、`2025-03-26` 或 `2024-11-05`，就原样返回，否则返回 `2025-06-18`。支持的方法：`initialize`、`ping`、`tools/list`、`tools/call`、`resources/list`、`resources/read`、`resources/templates/list`（始终为空）以及 `notifications/*`；其他方法返回 `-32601`。工具执行失败时返回带 `isError: true` 的结果并附上原因，方便 AI 自行修正。

headless 宿主使用 stdio：stdin 和 stdout 上每行一条 JSON-RPC 消息。dnSpy 内的服务器在同一个端口上提供三种 HTTP 传输：

| 传输 | 端点 | 适用于 |
|------|------|--------|
| Streamable HTTP（2025-03-26） | `/` 或 `/mcp` 上的 `POST` / `GET` / `DELETE`，会话 ID 放在 `Mcp-Session-Id` 头里 | Claude Code、codex 及大多数新版客户端 |
| SSE（2024-11-05） | `GET /sse`，然后 `POST /message?sessionId=<id>` | MCP Inspector、旧版客户端 |
| 普通 JSON-RPC | `POST /`，`Accept` 中不含 `text/event-stream` | `curl`、脚本 |

会话只保存在 dnSpy 的内存里。客户端在 dnSpy 重启后继续使用旧的会话 ID 时，服务器会接受它而不是拒绝，客户端不必重新连接（基于官方 TypeScript SDK 的客户端自己不会重新初始化）；只有客户端用 `DELETE` 结束的会话才会返回 `404`。

```bash
curl -s http://localhost:3000/health
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

## 开发

扩展需要在 [dnSpyEx](https://github.com/dnSpyEx/dnSpy) 的检出目录里构建，并使用 CI 固定的标签（工作流里的 `DNSPY_REF`，目前是 `v6.6.0`）：

```bash
git clone --recursive --branch v6.6.0 https://github.com/dnSpyEx/dnSpy.git
cd dnSpy
git clone https://github.com/KernelErr/dnSpy.Extension.MCP.git Extensions/dnSpy.Extension.MCP
cd Extensions/dnSpy.Extension.MCP

dotnet build -c Release                     # 扩展，net48 + net10.0-windows
dotnet build headless -c Release            # headless 宿主（会顺带构建扩展）
dotnet build -c Debug -f net10.0-windows    # 只构建一个 TFM，迭代更快
```

要在 dnSpy 里试用构建结果，把 `bin/<Config>/<TFM>/dnSpy.Extension.MCP.x.dll` 复制到 `<dnSpy>\bin\Extensions\dnSpy.Extension.MCP\`。

**改动必须在两种模式下都通过端到端测试。** 测试针对上层检出目录里构建的 dnSpy 运行，所以要先构建它：在 dnSpy 根目录运行 `./build.ps1 -buildtfm net`（跑 net48 时用 `-buildtfm netframework`；只装了 .NET SDK 时加 `-NoMsbuild`）：

```powershell
pwsh tests/fixtures/run-tests.ps1                  # 在 dnSpy 内，走 HTTP
pwsh tests/fixtures/run-tests.ps1 -Headless        # headless 宿主，走 stdio
pwsh tests/fixtures/run-tests.ps1 -Tfm net48       # 任一模式，针对 .NET Framework 版
```

测试会构建一个小的测试程序集（`tests/fixtures/TestIL.cs`），加载后调用所有工具，一直测到打补丁、保存、运行保存后的 DLL。CI 不跑这套测试，合并前请在两种模式下各跑一遍。

架构、约定及其背后的原因见 [CLAUDE.md](CLAUDE.md)。

### 项目结构

```
dnSpy.Extension.MCP/
├── .github/workflows/          build.yml（每次 push / PR）、release.yml（发布 Release 时）
├── McpServer.cs                HTTP 服务器：普通 JSON-RPC、SSE、Streamable HTTP、端口回退
├── McpDispatcher.cs            两种宿主共用的 JSON-RPC 分派
├── McpProtocol.cs              JSON-RPC / MCP 数据类型
├── IMcpHost.cs                 工具需要宿主提供的能力
├── DnSpyMcpHost.cs             dnSpy 内的宿主实现：文档、反编译器、树/标签页刷新
├── McpTools*.cs                32 个工具（分析、IL、字符串、交叉引用、重命名）
├── McpSettings*                设置页、设置持久化与日志
├── BepInExResources.cs         内置的六份指南
├── TheExtension.cs             入口：dnSpy 加载时启动服务器
├── headless/                   headless 宿主与 deploy-headless.ps1
├── tests/check-host-deps.ps1   net48 依赖版本守卫（CI 会运行）
└── tests/fixtures/             测试程序集与端到端测试
```

## CI 与发布

- `build.yml`：每次 push 和 PR 都会运行，先做 net48 依赖检查（`tests/check-host-deps.ps1`），再以 Debug 和 Release 构建扩展和 headless 宿主。
- `release.yml`：在 GitHub 上**发布** Release 时运行，只推送标签不会触发。它会按固定的标签构建 dnSpy，把发布版本号写入扩展，把扩展和 headless 宿主部署进每个包，再把压缩包和 DLL 附到该 Release 上。

```bash
git tag v0.1.15 && git push origin v0.1.15
gh release create v0.1.15 --title v0.1.15 --notes "..."   # 发布 Release 才会触发 release.yml
```

## License

与 dnSpyEx 相同，详情见 [dnSpyEx 仓库](https://github.com/dnSpyEx/dnSpy)。

## 致谢

- [dnSpyEx](https://github.com/dnSpyEx/dnSpy) — .NET 调试器与程序集编辑器
- [Model Context Protocol](https://modelcontextprotocol.io/) — Anthropic 的 MCP 规范
- [BepInEx](https://github.com/BepInEx/BepInEx) — Unity 游戏 modding 框架
