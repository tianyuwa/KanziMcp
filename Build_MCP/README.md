# Kanzi MCP Server

通过 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 让 AI 助手（Claude Code、Cursor 等）直接查询和操作 **Kanzi Studio** 项目：查节点、改属性、创建/删除节点、导入资源、审计绑定等。

> **当前状态（2026-06）**：MCP 全链路已跑通。Server 与 Plugin 之间使用 **TCP `127.0.0.1:9595`** 通信（类名仍保留 `Pipe` 前缀，实际已是 TCP）。

---

## 目录

- [架构概览](#架构概览)
- [代码框架](#代码框架)
- [实现原理](#实现原理)
- [MCP 工具清单](#mcp-工具清单)
- [环境要求](#环境要求)
- [编译与部署](#编译与部署)
- [Claude / Cursor MCP 配置](#claude--cursor-mcp-配置)
- [使用方法](#使用方法)
- [测试与调试](#测试与调试)
- [故障排查](#故障排查)
- [扩展开发](#扩展开发)
- [可选：OSS 远程桥接](#可选oss-远程桥接)

---

## 架构概览

系统由 **三个层次** 组成，MCP Server 本身不直接调用 Kanzi API，只做协议转换和可靠传输。

```
┌──────────────────────────────────────────────────────────────────┐
│  AI 客户端                                                        │
│  Claude Code / Cursor / 其他 MCP Client                          │
│  通信方式: JSON-RPC 2.0 over stdin/stdout                        │
└────────────────────────────┬─────────────────────────────────────┘
                             │
                             ▼
┌──────────────────────────────────────────────────────────────────┐
│  KanziMcpServer.exe（独立进程，.NET 10，自包含发布）               │
│  Program.cs → McpProtocolHandler → ToolHandler → KanziPipeClient │
└────────────────────────────┬─────────────────────────────────────┘
                             │  TCP 127.0.0.1:9595
                             │  一行 JSON 请求 → 一行 JSON 响应
                             ▼
┌──────────────────────────────────────────────────────────────────┐
│  PluginKanziMCP.dll（Kanzi Studio 进程内，.NET Framework 4.8）    │
│  KanziMcpPlugin → KanziTcpServer → KanziService                  │
└────────────────────────────┬─────────────────────────────────────┘
                             │  PluginInterface API
                             ▼
┌──────────────────────────────────────────────────────────────────┐
│  Kanzi Studio / Kanzi Engine                                     │
└──────────────────────────────────────────────────────────────────┘
```

**关键设计点：**

| 点 | 说明 |
|----|------|
| 双进程隔离 | MCP Server 是独立 exe，不依赖 Kanzi 进程；Kanzi 崩溃不影响 AI 客户端 |
| TCP 而非 Named Pipe | 绕过跨进程安全上下文限制，固定端口 `9595` |
| preview / apply | 改属性、删节点等操作支持预览模式，避免 AI 误改项目 |
| stderr 日志 | 所有调试日志写 stderr，stdout 只输出 JSON，符合 MCP 规范 |

---

## 代码框架

### 仓库结构

```
kanziMcpServer/
├── kanziMcpServer.sln              # 解决方案（2 个项目）
├── publish.bat                     # 一键编译 + 打包到 Build_MCP/
├── PluginInterface.dll             # 编译插件用（从 pluginInterface/ 复制，本地需有）
├── pluginInterface/
│   └── kanzi3.9.10/
│       └── PluginInterface.dll     # 按 Kanzi 版本存放
│
├── src/KanziMcpServer/             # MCP Server（.NET 10 可执行文件）
│   ├── Program.cs                  # 入口：stdin/stdout 主循环
│   ├── Handlers/
│   │   ├── McpProtocolHandler.cs   # JSON-RPC 协议路由
│   │   └── ToolHandler.cs          # 17 个 MCP 工具定义与执行
│   ├── Services/
│   │   └── KanziPipeClient.cs      # TCP 客户端（发/收 JSON）
│   └── Models/
│       ├── JsonRpcModels.cs        # 请求/响应 + McpConstants
│       └── NodeModels.cs           # 节点、属性、绑定模型
│
├── src/KanziMcpPlugin/             # Kanzi 插件（.NET 4.8 类库）
│   ├── KanziMcpPlugin.cs           # MEF 入口，启动 TCP 服务
│   ├── KanziMcpWindow.cs           # Studio 侧边栏 UI
│   ├── PipeServer/
│   │   └── KanziPipeServer.cs      # KanziTcpServer + 兼容别名
│   ├── Services/
│   │   ├── KanziService.cs         # 核心业务（7000+ 行）
│   │   └── KanziApiDumper.cs       # 启动时 API 反射导出（调试）
│   └── lib/                        # System.Text.Json 等依赖 DLL
│
├── test_mcp_client.py              # 交互式 MCP 测试客户端
├── analyze_plugin_*.py             # PluginInterface 分析脚本
└── .mcp.json                       # 项目级 MCP 配置（Claude Code）
```

### 各层职责

| 层级 | 文件 | 职责 |
|------|------|------|
| 入口 | `Program.cs` | 解析命令行、组装依赖、读 stdin 写 stdout |
| 协议 | `McpProtocolHandler.cs` | 解析 JSON-RPC，`method` 分发到 handler |
| 工具 | `ToolHandler.cs` | 声明工具 Schema，参数解析，调用 PipeClient |
| 传输 | `KanziPipeClient.cs` | TCP 连接、重试、超时、`SendRequestAsync` |
| 插件入口 | `KanziMcpPlugin.cs` | MEF `[Export]`，Initialize 时启动 TCP Server |
| TCP 服务 | `KanziPipeServer.cs` | 监听 9595，路由 `method` 到 KanziService |
| 业务 | `KanziService.cs` | 反射调用 Kanzi API，实现所有工具逻辑 |

---

## 实现原理

### 1. MCP 握手流程

AI 客户端连接后会按 MCP 规范依次发送：

```
客户端                          KanziMcpServer
  │── initialize ──────────────►│  返回 protocolVersion、capabilities、instructions
  │── initialized ─────────────►│  确认
  │── tools/list ──────────────►│  返回 17 个 kanzi_* 工具定义（含 inputSchema）
  │── tools/call ──────────────►│  执行具体工具，返回 content + isError
```

### 2. 一次 tools/call 的完整链路

以 `kanzi_query_nodes` 为例：

```
1. AI 发 JSON（stdin）:
   {"jsonrpc":"2.0","id":1,"method":"tools/call",
    "params":{"name":"kanzi_query_nodes","arguments":{"type":"TextBlock2D"}}}

2. McpProtocolHandler.HandleToolsCallAsync
   → ToolHandler.ExecuteToolAsync("kanzi_query_nodes", args)
   → 解析为 NodeQueryFilter

3. KanziPipeClient.QueryNodesAsync(filter)
   → SendRequestAsync: {"method":"query_nodes","args":{...}}
   → TCP 写入 Kanzi 插件

4. KanziTcpServer 收到请求
   → KanziService.QueryNodes(args)
   → 在 Kanzi 项目里反射查节点

5. 返回 {"result":"{...节点 JSON...}"}
   → Server 包装为 MCP 格式:
   {"content":[{"type":"text","text":"..."}],"isError":false}
   → 写 stdout
```

### 3. TCP 协议格式（Server ↔ Plugin）

- **地址**：`127.0.0.1:9595`
- **格式**：每行一条 JSON，UTF-8 无 BOM，换行分隔
- **请求**：`{"method":"query_nodes","args":{...}}`
- **成功响应**：`{"result": ...}`
- **失败响应**：`{"error":"错误信息"}`

### 4. 可靠性机制

| 机制 | 位置 | 行为 |
|------|------|------|
| 懒连接 | `KanziPipeClient` | 启动时后台连 TCP，失败不阻塞；首次请求再连 |
| 连接重试 | `ConnectAsync` | 最多 2 次，间隔 2–3 秒 |
| 请求重试 | `SendRequestAsync` | 超时/断线后重连，指数退避 2s/4s |
| 读超时 | 默认 120s | 复杂反射查询可能较慢 |

### 5. 日志位置

| 日志 | 路径 | 内容 |
|------|------|------|
| MCP Server | stderr | `[KanziMcpServer]`、`[KanziPipeClient]` |
| Kanzi 插件 | `C:\temp\KanziMcpPlugin.log` | 插件加载、TCP 连接、请求处理 |
| API Dump | `C:\temp\KanziApiDump.txt` | 启动时反射导出的 Kanzi API（调试用） |

---

## MCP 工具清单

共 **17 个工具**，均在 `ToolHandler.GetToolDefinitions()` 中注册。

### 查询类

| 工具名 | 说明 |
|--------|------|
| `kanzi_query_nodes` | 按 type/name/path 查询节点，可选含属性/绑定 |
| `kanzi_get_node_tree` | 获取层级节点树（可指定 rootPath、depth） |
| `kanzi_list_node_types` | 列出所有可用节点类型 |
| `kanzi_search_nodes` | 全文搜索（Name/Path/Type/Text） |
| `kanzi_get_binding_info` | 获取指定节点的数据绑定详情 |

### 属性操作

| 工具名 | 说明 |
|--------|------|
| `kanzi_set_node_property` | 设置单个属性（`preview` / `apply`） |
| `kanzi_batch_set_property` | 按 filter 批量设置属性 |
| `kanzi_get_property_metadata` | 获取某节点类型的属性元数据 |

### 节点 CRUD

| 工具名 | 说明 |
|--------|------|
| `kanzi_create_node` | 在父节点下创建新节点 |
| `kanzi_delete_node` | 删除节点（支持 preview，会删子树） |

### 资源

| 工具名 | 说明 |
|--------|------|
| `kanzi_import_image` | 导入图片到资源库 |
| `kanzi_import_fbx` | 导入 FBX 模型 |
| `kanzi_doctor_resource` | 诊断未使用的 Image/Texture |

### 审计

| 工具名 | 说明 |
|--------|------|
| `kanzi_audit_bindings` | 审计数据绑定（孤儿、优先级冲突） |
| `kanzi_audit_localization` | 审计多语言覆盖 |
| `kanzi_audit_project_structure` | 审计命名规范、层级深度 |
| `kanzi_audit_resource_references` | 审计资源引用（未用/损坏/孤儿） |

### 状态

| 工具名 | 说明 |
|--------|------|
| `kanzi_get_status` | MCP Server 与 Kanzi TCP 连接状态 |

> **安全提示**：涉及修改的操作默认 `mode: "preview"`。建议 AI 先 preview 确认，再 `apply`。

---

## 环境要求

| 组件 | 要求 |
|------|------|
| 操作系统 | Windows（Kanzi Studio 仅支持 Windows） |
| Kanzi Studio | 3.9.10（或其他版本，需对应 PluginInterface.dll） |
| .NET SDK | .NET 10（编译 Server）；.NET Framework 4.8（编译 Plugin，通常随 VS 安装） |
| Python 3 | 可选，用于 `test_mcp_client.py` 测试 |

---

## 编译与部署

### 方式一：一键发布（推荐）

```powershell
# 在项目根目录执行，默认 Kanzi 3.9.10
.\publish.bat

# 指定其他 Kanzi 版本（目录名需为 pluginInterface\kanzi3.x.x\）
.\publish.bat 3.9.10
```

脚本会自动执行 `dotnet restore` → 编译插件 → 发布 Server。若曾清理过 `obj/` 目录，restore 步骤会重新生成 `project.assets.json`（**不要**在 restore 之前使用 `--no-restore`）。

`build_and_upload.bat` 会先调用 `publish.bat`，成功后再上传到 OSS。

输出目录 `Build_MCP/`：

```
Build_MCP/
├── KanziMcpServer/
│   └── KanziMcpServer.exe      # 自包含，无需单独安装 .NET
├── KanziMcpPlugin/
│   └── kanzi3.9.10/
│       ├── PluginKanziMCP.dll
│       └── System.Text.Json.dll 等依赖
└── test_mcp_client.py
```

### 方式二：手动 dotnet 编译

```powershell
# 1. 确保根目录有 PluginInterface.dll
copy pluginInterface\kanzi3.9.10\PluginInterface.dll PluginInterface.dll

# 2. 编译插件
dotnet build src\KanziMcpPlugin\KanziMcpPlugin.csproj -c Release

# 3. 发布 Server（自包含）
dotnet publish src\KanziMcpServer\KanziMcpServer.csproj -c Release -r win-x64 --self-contained -o Build_MCP\KanziMcpServer
```

### 部署 Kanzi 插件

将以下文件复制到 Kanzi 插件目录（**所有依赖 DLL 一起复制**）：

```
C:\ProgramData\Rightware\Kanzi 3.9.10\plugins\
├── PluginKanziMCP.dll
├── System.Text.Json.dll
├── System.Memory.dll
└── ...（lib 目录下其余 DLL）
```

重启 Kanzi Studio，在菜单或插件面板中确认 **Kanzi MCP** 已加载。

---

## Claude / Cursor MCP 配置

### 前置条件

1. Kanzi Studio 已启动，且 MCP 插件已加载（TCP 9595 在监听）
2. Kanzi 中已打开一个项目
3. `KanziMcpServer.exe` 路径已知（如 `Build_MCP\KanziMcpServer\KanziMcpServer.exe`）

### Claude Code（项目级 `.mcp.json`）

在项目根目录创建或编辑 `.mcp.json`：

```json
{
  "mcpServers": {
    "kanzi": {
      "command": "C:/Users/WTY/WorkBuddy/kanziMcpServer/Build_MCP/KanziMcpServer/KanziMcpServer.exe",
      "args": ["--verbose"],
      "env": {
        "KANZI_CONNECT_TIMEOUT": "5000"
      }
    }
  }
}
```

或在 Claude Code 对话中使用：

```
/mcp add kanzi "C:/path/to/KanziMcpServer.exe" -- --verbose
```

**说明：**

| 字段 | 说明 |
|------|------|
| `command` | `KanziMcpServer.exe` 的**绝对路径**（建议用 `/` 或转义 `\`） |
| `args` | 可选：`--verbose` 详细日志；`--no-auto-connect` 禁用启动时连接 |
| `env.KANZI_PIPE_NAME` | 历史兼容名，实际解析为 TCP 端口；`tcp:9595` 可改端口 |
| `env.KANZI_CONNECT_TIMEOUT` | TCP 连接超时（毫秒），默认 5000 |

### Cursor

打开 **Cursor Settings → MCP**，添加 Server：

```json
{
  "mcpServers": {
    "kanzi": {
      "command": "C:\\Users\\WTY\\WorkBuddy\\kanziMcpServer\\Build_MCP\\KanziMcpServer\\KanziMcpServer.exe",
      "args": ["--verbose"]
    }
  }
}
```

或在用户级配置 `%USERPROFILE%\.cursor\mcp.json` 中添加上述内容。

配置完成后重启 Cursor / 重载 MCP，在 Agent 模式即可看到 `kanzi_*` 工具。

### 验证 MCP 是否连通

```powershell
# 手动发 initialize（stderr 会打日志）
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | .\Build_MCP\KanziMcpServer\KanziMcpServer.exe --verbose
```

或在 AI 对话中说：

> 用 kanzi_get_status 检查 Kanzi 连接状态

期望返回 `connected: true` 且 `port: 9595`。

---

## 使用方法

### 典型工作流

1. **启动 Kanzi Studio** → 打开项目 → 确认插件 TCP 已启动
2. **启动 AI 客户端** → MCP 自动 fork `KanziMcpServer.exe`
3. **在对话中调用工具**，例如：

```
# 查看节点树
用 kanzi_get_node_tree，depth 设为 2

# 查找所有 TextBlock2D
用 kanzi_query_nodes，type 为 TextBlock2D，includeProperties 为 true

# 修改文字颜色（先预览）
用 kanzi_set_node_property，path="/Screen/Title"，property="FontColor"，
value={"r":1,"g":0,"b":0,"a":1}，mode="preview"

# 确认无误后 apply
mode 改为 apply 再执行一次
```

### KanziMcpServer 命令行参数

```
KanziMcpServer [options]

  --help, -h              显示帮助
  --version               显示版本
  --pipe, -p <name>       TCP 端口配置（默认 9595；格式 tcp:9595）
  --timeout, -t <ms>      连接超时（默认 5000ms）
  --verbose, -v           详细日志（输出到 stderr）
  --no-auto-connect       启动时不后台连接 Kanzi
```

---

## 测试与调试

### 交互式测试客户端

```powershell
python test_mcp_client.py --server "Build_MCP\KanziMcpServer\KanziMcpServer.exe"
```

内置命令：`init`、`tools`、`status`、`tree`、`nodes`、`search`、`set`、`apply` 等。

### 分析 Kanzi PluginInterface

```powershell
python analyze_plugin_interface.py
python analyze_plugin_metadata.py
```

### 查看插件日志

```powershell
Get-Content C:\temp\KanziMcpPlugin.log -Tail 50 -Wait
```

---

## 故障排查

| 现象 | 可能原因 | 处理 |
|------|----------|------|
| `Cannot connect to Kanzi TCP Server` | Studio 未启动或插件未加载 | 重启 Studio，检查 plugins 目录 DLL 是否齐全 |
| 工具返回 timeout | 项目过大或 Kanzi 繁忙 | 增大 `KANZI_CONNECT_TIMEOUT`；查 `KanziMcpPlugin.log` |
| MCP 客户端看不到 kanzi 工具 | `.mcp.json` 路径错误或 exe 不存在 | 检查 `command` 绝对路径；手动运行 exe 测 stderr |
| stdout 混入非 JSON | 误用 `Console.WriteLine` 打日志 | 日志必须写 stderr（代码已遵守） |
| 插件加载失败 | 缺少 `lib/*.dll` | 将 `src/KanziMcpPlugin/lib/` 下所有 DLL 复制到 plugins 目录 |
| `NETSDK1004` 找不到 assets 文件 | 删除了 `obj/` 但脚本用了 `--no-restore` | 使用最新 `publish.bat`（已含 restore 步骤），或手动 `dotnet restore` |
| 9595 端口被占用 | 其他程序占用 | 修改 Server/Plugin 端口（需改代码常量）或释放端口 |

---

## 扩展开发

### 添加新 MCP 工具（四步）

1. **`KanziService.cs`**：实现业务方法，处理 `JsonElement args`，返回 JSON 字符串
2. **`KanziPipeServer.cs`**：在 `ProcessRequest` 的 switch 中增加 `method` 分支
3. **`KanziPipeClient.cs`**：增加公开方法，调用 `SendRequestAsync`
4. **`ToolHandler.cs`**：增加 `GetXxxTool()` 定义 + `ExecuteXxxAsync` + switch 分支

### 自定义 JSON-RPC 方法（非 tools/call）

`McpProtocolHandler` 还支持直连方法（较少用）：

- `kanzi/query_nodes`、`kanzi/set_property` 等

正常 AI 客户端走 `tools/call` 即可。

---

## 可选：OSS 远程桥接

若 Claude 与 Kanzi 不在同一台机器，仓库内还提供 OSS 中转方案（**非核心功能**）：

| 文件 | 作用 |
|------|------|
| `kanzi_mcp_proxy.py` | 本机 MCP → 阿里云 OSS |
| `oss_bridge_daemon.py` | Kanzi 机器 OSS → KanziMcpServer.exe |
| `build_and_upload.bat` | 编译并上传到 OSS |

本地开发直接使用 TCP 直连即可，无需 OSS 组件。

---

## 许可证

MIT License
