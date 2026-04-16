# dnSpy MCP 扩展

[![Build](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/build.yml)
[![Release](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml/badge.svg)](https://github.com/KernelErr/dnSpy.Extension.MCP/actions/workflows/release.yml)

一个用于 [dnSpyEx](https://github.com/dnSpyEx/dnSpy) 的 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 扩展，向 Claude 等 AI 助手暴露 .NET 程序集分析能力。

English: see [README.md](README.md).

## 功能

### MCP 工具（共 10 个）

1. **list_assemblies** — 列出所有已加载的程序集及其元数据
2. **get_assembly_info** — 查看指定程序集的详细信息（命名空间分页）
3. **list_types** — 列出程序集或命名空间下的所有类型（分页）
4. **get_type_info** — 获取类型的字段、属性及分页的方法
5. **get_type_fields** — 按通配符匹配类型的字段（如 `*Bonus*`）
6. **get_type_property** — 获取属性的详细信息，包含 get/set 访问器
7. **find_path_to_type** — 基于字段/属性对两个类型做 BFS 路径搜索
8. **decompile_method** — 将方法反编译为 C#
9. **search_types** — 跨程序集按通配符或子串搜索类型
10. **generate_bepinex_plugin** — 生成带 Harmony 钩子的 BepInEx 插件模板

### MCP 资源（共 6 个）

内嵌的 BepInEx 开发文档，通过 `resources/list` / `resources/read` 提供：

1. **plugin-structure** — 插件基本结构
2. **harmony-patching** — HarmonyX 补丁指南（Prefix/Postfix/Transpiler）
3. **configuration** — 配置系统用法
4. **common-scenarios** — 常见开发场景
5. **il2cpp-guide** — IL2CPP 开发指南
6. **mono-vs-il2cpp** — Mono 与 IL2CPP 对比及迁移

所有文档都内嵌在 DLL 中，**离线可用**。

## 安装

### 从 Release 下载

1. 到 [Releases](https://github.com/KernelErr/dnSpy.Extension.MCP/releases) 页面下载最新构建产物。
2. 根据 dnSpy 的运行时选择对应 DLL：
   - `dnSpy.Extension.MCP-net48.dll` — .NET Framework 4.8 版 dnSpy
   - `dnSpy.Extension.MCP-net10.0-windows.dll` — .NET 10 版 dnSpy
3. 重命名为 `dnSpy.Extension.MCP.x.dll`（`.x` 后缀是 dnSpy 加载扩展的必要标记）。
4. 在 `<dnSpy 安装目录>\bin\Extensions\` 下新建一个名为 `dnSpy.Extension.MCP` 的文件夹，把 DLL 放进去。
5. 重启 dnSpy。

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
- **Port** — 首选 TCP 端口（默认 `3000`）。若端口已被占用，扩展会自动尝试 `port + 1`，最多 20 次，并在日志中记录最终绑定的端口。查看 Server Log 面板（或磁盘备用日志）以确认真实端口。
- **Host** — 绑定地址（默认 `localhost`）。

## 传输协议

两种传输使用同一个 `HttpListener`、同一端口。

### 普通 HTTP JSON-RPC

一次性请求/响应：向 `/` POST 一个 JSON-RPC 消息，从同一 HTTP 响应体读取结果。

```bash
curl -s http://localhost:3000/health
# {"status":"ok","service":"dnSpy MCP Server"}

curl -s -X POST http://localhost:3000/ \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
```

### Server-Sent Events（MCP 2024-11-05）

双端点传输：一条长连接 SSE 流 + 一个用于客户端消息的 POST 端点。

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

### Claude Desktop 配置

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
├── McpServer.cs            HttpListener 上的 HTTP + SSE 服务器，含端口自动回退
├── McpProtocol.cs          JSON-RPC 2.0 / MCP 数据模型
├── McpTools.cs             10 个 MCP 工具（封装 dnSpy 服务）
├── McpSettings.cs          设置视图模型 + 持久化 + 文件日志
├── McpSettingsPage.cs      实现 IAppSettingsPageProvider，接入 dnSpy 设置界面
├── BepInExResources.cs     内嵌的 BepInEx 文档（6 份资源）
├── TheExtension.cs         IExtension 入口，Loaded 时启动服务器
└── dnSpy.Extension.MCP.csproj
```

### 架构要点

- **目标框架**：`net48` 与 `net10.0-windows`（继承自 `DnSpyCommon.props`）。
- **传输**：单个 `HttpListener` 同时承载普通 HTTP JSON-RPC 与 SSE 两条路径。**不**使用 Kestrel — dnSpy 的自包含 .NET 发布版不会捆绑 ASP.NET Core，任何对 `Microsoft.AspNetCore.*` 的引用都会让 MEF 在组合 `IExtension` 时抛出静默的 `TypeLoadException`，扩展入口因此无法实例化。
- **MEF**：服务使用 `[Export(typeof(T))]` + `[ImportingConstructor]`。不要手动 `new` `McpServer` / `McpSettings` / `McpTools`。
- **错误码**：工具处理函数抛 `ArgumentException` → JSON-RPC `-32602`（参数非法）；其他异常 → `-32603`（服务端错误）。
- **日志**：`McpSettings.Log(...)` 同时写 UI 日志面板和磁盘回退文件。磁盘日志是权威来源 — 即使 WPF 派发器还没启动、或者设置对话框没打开，它也能记录。

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
