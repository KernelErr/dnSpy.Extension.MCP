# dnSpy MCP 扩展

[![Build](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml)
[![Release](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml)

一个用于 [dnSpyEx](https://github.com/dnSpyEx/dnSpy) 的 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 扩展，向 Claude 等 AI 助手暴露 .NET 程序集的**分析能力**与 **IL 编辑能力**。

English: see [README.md](README.md).

## 功能

### MCP 工具（共 15 个）

#### 分析与导航

1. **list_assemblies** — 列出所有已加载的程序集及其元数据
2. **get_assembly_info** — 查看指定程序集的详细信息（命名空间分页）
3. **list_types** — 列出程序集或命名空间下的所有类型（分页）
4. **get_type_info** — 获取类型的字段、属性及分页的方法（方法条目含 `token` / `MDToken`，用于无歧义定位）
5. **list_methods** — 列出类型的全部方法，每条含 `token` 与 `parameter_types`，分页
6. **get_type_fields** — 按通配符匹配类型的字段（如 `*Bonus*`）
7. **get_type_property** — 获取属性的详细信息，包含 get/set 访问器
8. **search_types** — 跨程序集按通配符或子串搜索类型
9. **find_path_to_type** — 基于字段/属性对两个类型做 BFS 路径搜索
10. **decompile_method** — 将方法反编译为 C#（可通过 `parameter_types` / `method_token` 精确区分重载）

#### IL 查看与编辑（0.1.3 新增）

1. **get_method_il** — 方法 IL 指令（index、offset、opcode、operand）+ 局部变量 + 异常处理块 + 方法体标志
2. **patch_method_il** — 按序执行 `replace` / `insert` / `delete` / `set_init_locals` 编辑；首次补丁会自动快照
3. **revert_method_il** — 回滚到补丁前的方法体
4. **save_assembly** — 将模块写回磁盘（覆盖原文件时会自动生成带时间戳的备份，`NativeWrite` 保留本机 stub / Win32 资源 / 延迟加载导入，GAC 路径被拒绝）

#### 代码生成

1. **generate_bepinex_plugin** — 生成带 Harmony 钩子的 BepInEx 插件模板

### MCP 资源（共 6 个）

内嵌的 BepInEx 开发文档，通过 `resources/list` / `resources/read` 提供：

1. **plugin-structure** — 插件基本结构
2. **harmony-patching** — HarmonyX 补丁指南（Prefix/Postfix/Transpiler）
3. **configuration** — 配置系统用法
4. **common-scenarios** — 常见开发场景
5. **il2cpp-guide** — IL2CPP 开发指南
6. **mono-vs-il2cpp** — Mono 与 IL2CPP 对比及迁移

所有文档都内嵌在 DLL 中，**离线可用**。

## IL 查看与编辑

AI 客户端可以像使用 dnSpy "编辑方法实体" 对话框一样读取、修改、保存字节码。

### 操作数语法（带标签前缀）

每条指令的操作数都是一个带标签的字符串；`get_method_il`（读）与 `patch_method_il`（写）共用同一套语法，因此操作数可以无损往返。

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

`calli` / `InlineSig` 在 0.1.3 中暂不支持。

### 端到端示例：修改常量并落盘

假设 `TestIL.dll` 中有 `public static int AddOne(int x) => x + 1;`。

```bash
# 1. 定位方法（parameter_types 可用于区分重载）
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" -d '{
  "jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"list_methods",
    "arguments":{"assembly_name":"TestIL","type_full_name":"TestIL.Simple"}}}'

# 2. 读取 IL
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" -d '{
  "jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"get_method_il",
    "arguments":{"assembly_name":"TestIL","type_full_name":"TestIL.Simple","method_name":"AddOne"}}}'
# 返回的 instructions 里会有：{"index":1,"opcode":"ldc.i4.1","operand":""}

# 3. 把 +1 改成 +41
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" -d '{
  "jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"patch_method_il",
    "arguments":{"assembly_name":"TestIL","type_full_name":"TestIL.Simple","method_name":"AddOne",
      "edits":[{"op":"replace","index":1,"opcode":"ldc.i4","operand":"int:41"}]}}}'

# 4. 保存。覆盖原文件前会先生成 <path>.<yyyyMMdd-HHmmss>.bak 备份
curl -s -X POST http://localhost:3000/ -H "Content-Type: application/json" -d '{
  "jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"save_assembly",
    "arguments":{"assembly_name":"TestIL"}}}'
```

重新加载保存后的 DLL，`AddOne(10)` 将返回 **`51`**，而不是原本的 **`11`**。

### 注意事项

- **没有 Ctrl+Z**。`patch_method_il` 不走 dnSpy 的撤销栈，想回退请用 `revert_method_il` — 每个方法在第一次被补丁时自动建立快照，revert 后或一次成功 save 后快照会被清理。
- **保存后 dnSpy 的内存视图不会自动刷新**。要在当前 dnSpy 窗口里看到落盘后的状态，需要重新打开该程序集。
- **GAC 路径会被拒绝**。保存 `mscorlib` 等 GAC 程序集会返回 `-32602` 错误。
- **仅限指令层面**。添加/删除局部变量或异常处理块不在 0.1.3 范围内；`get_method_il` 会以只读形式暴露它们。

## 安装

### 推荐方式：开箱即用的整合包

打开 [Releases](https://github.com/KernelErr/dnSpy.Extension.MCP/releases) 页面，下载与你系统匹配的整合包 — **扩展已放在正确的位置，不需要操心路径**：

| 文件 | 内容 | 运行时要求 |
|------|------|-------------|
| `dnSpy-MCP-win-x64.zip` | dnSpy .NET 10 自包含 x64 + MCP 扩展 | 无需 — 运行时已内含 |
| `dnSpy-MCP-win-x86.zip` | dnSpy .NET 10 自包含 x86 + MCP 扩展 | 无需 — 运行时已内含 |
| `dnSpy-MCP-net48.zip` | dnSpy .NET Framework 4.8 版 + MCP 扩展 | .NET Framework 4.8（Windows 10+ 默认自带） |

1. 下载并解压到任意目录。
2. 双击 `dnSpy.exe`。
3. 打开**编辑 → 设置 → MCP Server**，勾选 **Enable Server**，点击确定。

搞定。如果你已经装好了 dnSpy、只想拿插件，参考下面的"仅插件"方式。

### 仅插件（已安装 dnSpy 的用户）

1. 根据 dnSpy 的运行时选择对应 DLL：
   - `dnSpy.Extension.MCP-net48.dll` — .NET Framework 4.8 版 dnSpy
   - `dnSpy.Extension.MCP-net10.0-windows.dll` — .NET 10 版 dnSpy
2. 重命名为 `dnSpy.Extension.MCP.x.dll`（`.x` 后缀是 dnSpy 加载扩展的必要标记）。
3. 在 `<dnSpy 安装目录>\bin\Extensions\` 下新建一个名为 `dnSpy.Extension.MCP` 的文件夹，把 DLL 放进去。
4. 重启 dnSpy。

**最终路径必须完全符合下面的层级** — 子文件夹名与 DLL 同名、保留 `.x.dll` 后缀、且恰好位于 `Extensions\` 下一层：

```
<dnSpy 安装目录>\
└── bin\
    └── Extensions\
        └── dnSpy.Extension.MCP\           ← 子文件夹（不存在则创建）
            └── dnSpy.Extension.MCP.x.dll  ← 带 .x 后缀的 DLL
```

假设 dnSpy 安装在 `C:\Tools\dnSpy`，最终路径应该是：

```
C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll
```

如果 DLL 直接放在 `bin\Extensions\` 下（没有子文件夹），或者丢了 `.x` 后缀，dnSpy 会静默忽略它，设置界面里也看不到 MCP Server 这一项。

### 从源码构建

```bash
# 克隆 dnSpyEx（必须带 --recursive 以初始化子模块）
git clone --recursive https://github.com/dnSpyEx/dnSpy.git
cd dnSpy

# 将本扩展克隆到 Extensions 目录
git clone https://github.com/KernelErr/dnSpy.Extension.MCP.git Extensions/dnSpy.Extension.MCP

# 构建（两个 TFM 都会编译）
cd Extensions/dnSpy.Extension.MCP
dotnet build -c Release

# 部署到 dnSpy 安装目录
cp bin/Release/net10.0-windows/dnSpy.Extension.MCP.x.dll \
   <dnSpy 安装目录>/bin/Extensions/dnSpy.Extension.MCP/
```

## 配置

配置入口：**编辑 → 设置 → MCP Server**

- **Enable Server** — 勾选并应用即可即时启动/停止 HTTP 服务器。
- **Port** — 首选 TCP 端口（默认 `3000`）。若端口已被占用，扩展会自动尝试 `port + 1`，最多 20 次，并在日志中记录最终绑定的端口。查看 Server Log 面板确认实际端口。
- **Host** — 绑定地址（默认 `localhost`）。

## 传输协议

三种传输共用同一个 `HttpListener` 与同一端口。服务器根据请求的路径、HTTP 方法与 `Accept` 头自动选择对应的处理逻辑。

### Streamable HTTP（MCP 2025-03-26）

单端点传输，codex 等新版 MCP 客户端使用。客户端在 POST 时携带 `Accept: application/json, text/event-stream`；服务器在 `initialize` 响应的 `Mcp-Session-Id` 头中分配会话 ID，后续请求需回传该头。同一端点的 `GET` 用于服务端主动推送（SSE），`DELETE` 用于显式结束会话。

路径 `/` 与 `/mcp` 均可作为端点。

```bash
# 1. 初始化 —— 服务器在 Mcp-Session-Id 响应头中返回会话 ID
curl -i -X POST http://localhost:3000/ \
  -H "Accept: application/json, text/event-stream" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
# HTTP/1.1 200 OK
# Mcp-Session-Id: <sid>
# Content-Type: application/json
# {"jsonrpc":"2.0","id":1,"result":{...}}

# 2. 后续请求需回传会话头
curl -X POST http://localhost:3000/ \
  -H "Accept: application/json, text/event-stream" \
  -H "Content-Type: application/json" \
  -H "Mcp-Session-Id: <sid>" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'

# 3. 可选：显式结束会话（服务器关闭时也会清理）
curl -X DELETE http://localhost:3000/ -H "Mcp-Session-Id: <sid>"
```

codex `~/.codex/config.toml`：

```toml
[mcp_servers.dnspy-mcp]
type = "streamable-http"
url = "http://localhost:3000"
```

### 普通 HTTP JSON-RPC

一次性请求/响应：向 `/` POST 一个 JSON-RPC 消息（`Accept` 头**不包含** `text/event-stream`），从同一 HTTP 响应体读取结果。适合 `curl` 调试或只会说纯 HTTP 的客户端。

```bash
curl -s http://localhost:3000/health
# {"status":"ok","service":"dnSpy MCP Server"}

curl -s -X POST http://localhost:3000/ \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
```

### Server-Sent Events（MCP 2024-11-05，遗留）

为了兼容 MCP Inspector 与旧客户端而保留的双端点传输：一条长连接 SSE 流 + 一个用于客户端消息的 POST 端点。

1. `GET /sse` — 打开 `text/event-stream`。首个事件 (`event: endpoint`) 的 `data` 字段告诉客户端应当 POST 到哪里（`/message?sessionId=<id>`）。
2. `POST /message?sessionId=<id>` — 客户端发送 JSON-RPC 请求，服务器立即返回 `202 Accepted`，真正的 JSON-RPC 响应作为 `event: message` 写回对应的 SSE 流。

```bash
# 终端 A：打开 SSE 流并保持
curl -N http://localhost:3000/sse
# event: endpoint
# data: /message?sessionId=<sessionId>
# ...（POST 到达后）...
# event: message
# data: {"jsonrpc":"2.0","id":1,"result":...}

# 终端 B：向对应会话发送请求
curl -X POST "http://localhost:3000/message?sessionId=<sessionId>" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
# HTTP 202 Accepted — 实际响应出现在终端 A 的 SSE 流里
```

### 客户端配置

#### Claude Code

命令行一键注册（自动走根路径下的 Streamable HTTP 传输）：

```bash
claude mcp add --transport http dnspy http://localhost:3000
# 验证是否注册成功：
claude mcp list
```

或在项目根目录写入 `.mcp.json`（把配置跟项目一起提交）：

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

在 Claude Code 里运行 `/mcp` 可以确认 `dnspy` 已连接，并查看它暴露的工具。

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

参见上文 "Streamable HTTP" 章节里的 `~/.codex/config.toml` 示例。

## 开发

```bash
# 单 TFM 构建，迭代更快
dotnet build -c Debug -f net48
dotnet build -c Debug -f net10.0-windows
```

### 项目结构

```
dnSpy.Extension.MCP/
├── .github/workflows/      GitHub Actions（构建与发布）
├── McpServer.cs            HttpListener：HTTP + SSE + Streamable HTTP + 端口自动回退
├── McpProtocol.cs          JSON-RPC 2.0 / MCP 数据模型
├── McpTools.cs             分析类工具 + MEF 导出 + 请求分派（sealed partial）
├── McpTools.IL.cs          IL 查看/补丁/回滚/保存 + 操作数渲染器与解析器
├── McpSettings.cs          设置视图模型 + 持久化 + 日志（磁盘日志仅 Debug 构建）
├── McpSettingsPage.cs      实现 IAppSettingsPageProvider，接入 dnSpy 设置界面
├── BepInExResources.cs     内嵌的 BepInEx 文档（6 份资源）
├── TheExtension.cs         IExtension 入口，Loaded 时启动服务器
├── tests/fixtures/         TestIL.cs + build-fixture.ps1 + run-tests.ps1（端到端测试）
└── dnSpy.Extension.MCP.csproj
```

### 架构要点

- **目标框架**：`net48` 与 `net10.0-windows`（继承自 `DnSpyCommon.props`）。
- **传输**：单个 `HttpListener` 同时承载普通 HTTP JSON-RPC、2024-11-05 SSE、2025-03-26 Streamable HTTP 三种协议，共用同一端口。**不**使用 Kestrel — dnSpy 的自包含 .NET 发布版不会捆绑 ASP.NET Core，任何对 `Microsoft.AspNetCore.*` 的引用都会让 MEF 在组合 `IExtension` 时抛出静默的 `TypeLoadException`，扩展入口因此无法实例化。
- **MEF**：服务使用 `[Export(typeof(T))]` + `[ImportingConstructor]`。不要手动 `new` `McpServer` / `McpSettings` / `McpTools`。
- **UI 线程调度**：`ExecuteTool` 里所有工具处理函数都通过 WPF Dispatcher 调度执行。`IDocumentTreeView` 的节点是 `DispatcherObject`，一旦有用户加载的程序集被索引，从 HTTP 工作线程直接访问就会抛 "calling thread cannot access this object"，因此必须统一 marshal；已经显式走 UI 线程的处理函数（patch、revert、save）被二次包裹也是安全的。
- **错误码**：工具处理函数抛 `ArgumentException` → JSON-RPC `-32602`（参数非法）；其他异常 → `-32603`（服务端错误）。
- **日志**：`McpSettings.Log(...)` 总会写 UI 日志面板，只在 **Debug** 构建下额外写入 `D:\dnspy-mcp.log`。Release 构建完全靠内存日志，终端用户机器无需可写 `D:` 盘。

## 协议

基于 [MCP](https://modelcontextprotocol.io/) `2024-11-05`，走 JSON-RPC 2.0。

支持的方法：`initialize`、`ping`、`tools/list`、`tools/call`、`resources/list`、`resources/read`，以及 `notifications/*`。

## CI / 发布

- `.github/workflows/build.yml` — 每次 push/PR 都会构建两个 TFM。
- `.github/workflows/release.yml` — 推送 `v*.*.*` 标签时构建 Release DLL 并附到 GitHub release。

```bash
git tag v1.0.0
git push origin v1.0.0
```

## 技术细节

- **依赖**：`dnSpy.Contracts.DnSpy`、`dnSpy.Contracts.Logic`、`dnlib`；`System.Text.Json`（`net48` 通过 NuGet 包，`net10.0-windows` 随 BCL）。
- **BFS 路径查找**：`find_path_to_type` 对每个类型的字段和属性做广度优先搜索。
- **反编译**：通过 `IDecompilerService` 使用 dnSpy 默认反编译器（默认 C#）。
- **IL 写盘**：`save_assembly` 对从磁盘加载的模块调用 `((ModuleDefMD)module).NativeWrite(path, NativeModuleWriterOptions)`（保留本机 stub、Win32 资源、延迟加载导入、混合代码）；对内存里新建的模块调用 `module.Write(path, ModuleWriterOptions)`。落盘前先通过 `peImage as dnlib.PE.IInternalPEImage` 关闭内存映射 I/O — `dnSpy.AsmEditor` 里的 `IMmapDisabler` 是 internal，因此直接内联一行调用，避免把 AsmEditor 作为依赖。
- **跨方法引用解析**：`patch_method_il` 里 `method:` / `field:` / `type:` 操作数的解析方式是遍历所有已加载模块按 `FullName` 精确匹配，再用 `new Importer(module, ImporterOptions.TryToUseDefs)` 导入到目标模块。

## 故障排查

### 设置页面出现但服务器不启动

最常见原因：`IExtension` 那一半在 MEF 组合时失败（而 `IAppSettingsPageProvider`，即设置页面那一半仍能正常组合）。典型症状：MCP Server 设置页面存在并且能勾选 Enable Server，但点击 OK 没反应、日志里什么都没出现。根因通常是运行时依赖缺失 — 先看磁盘回退日志，并确认部署的 DLL 与 dnSpy 当前的 TFM 对应。

### 端口被占用

服务器会自动尝试 `port + 1`，最多 20 次。在日志里查找 `Port N is in use; falling back to M`，客户端改连回退后的端口即可。

### 构建错误

- 确认 dnSpyEx 用 `--recursive` 克隆，且子模块已初始化。
- 在 dnSpyEx 仓库根目录先执行 `dotnet restore`。
- 需要 .NET 10 SDK（更早的 dnSpy 版本用 .NET 8；`DnSpyCommon.props` 是权威依据）。

## License

与 dnSpyEx 相同，详情见 [dnSpyEx 仓库](https://github.com/dnSpyEx/dnSpy)。

## 致谢

- [dnSpyEx](https://github.com/dnSpyEx/dnSpy) — .NET 调试器与程序集编辑器
- [Model Context Protocol](https://modelcontextprotocol.io/) — Anthropic 的 MCP 规范
- [BepInEx](https://github.com/BepInEx/BepInEx) — Unity 游戏 modding 框架
