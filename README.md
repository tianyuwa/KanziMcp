# Kanzi MCP Server

通过 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 让 AI 助手（Claude Code、Cursor 等）直接查询和操作 **Kanzi Studio** 项目：查节点、改属性、创建/删除节点、导入资源、审计绑定、创建状态机等。

> **当前状态（2026-06）**：MCP 全链路已跑通。Server 与 Plugin 之间使用 **TCP `127.0.0.1:9595`** 通信（类名仍保留 `Pipe` 前缀，实际已是 TCP）。共 **20 个 MCP 工具**，插件业务层采用 **SDK 优先（`Sdk.cs`）+ 反射兜底（`Reflection.cs` / `Mutate.Legacy.cs`）** 双轨架构，并按职责拆分为 partial class 多文件结构。状态机批量创建已接入 **Studio Batch Modification**（跨 MCP 批次共用一个 batch session），**500 个 State 约 30+ 秒**（优化前同规模需数十分钟）。

---

## 目录

- [架构概览](#架构概览)
- [2026-06 重构成果摘要](#2026-06-重构成果摘要)
- [关键技术](#关键技术)
- [代码框架](#代码框架)
- [实现原理](#实现原理)
- [MCP 工具清单](#mcp-工具清单)
- [环境要求](#环境要求)
- [编译与部署](#编译与部署)
- [Claude / Cursor MCP 配置](#claude--cursor-mcp-配置)
- [使用方法](#使用方法)
- [测试与调试](#测试与调试)
- [MCP 测试指令手册](MCP_Command_Use.md)
- [故障排查](#故障排查)
- [扩展开发与可扩展性](#扩展开发与可扩展性)
- [可选：OSS 远程桥接](#可选oss-远程桥接)

---

## 架构概览

系统由 **三个层次** 组成，MCP Server 本身不直接调用 Kanzi API，只做协议转换和可靠传输。

```
┌──────────────────────────────────────────────────────────────────┐
│  AI 客户端                                                        │
│  Claude Code / Cursor / 其他 MCP Client                          │
│  通信方式: JSON-RPC 2.0 over stdin/stdout（MCP 2024-11-05）     │
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
│  KanziMcpPlugin → KanziTcpServer → KanziService (partial)        │
│    ├─ Sdk.cs（SDK 优先层）→ 强类型 PluginInterface API           │
│    └─ Reflection.cs / Mutate.Legacy.cs（反射兜底）               │
└────────────────────────────┬─────────────────────────────────────┘
                             │  PluginInterface API（SDK 优先 + 反射兜底）
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
| SDK 优先 + 反射兜底 | 核心路径经 `Sdk.cs` 强类型调用；无公开 API 或 SDK 失败时降级到 `Reflection.cs` / `Mutate.Legacy.cs` |
| preview / apply | 改属性、删节点、创建状态机等操作支持预览模式，避免 AI 误改项目 |
| 分批执行 + Studio Batch | MCP 层 `batchIndex` / `autoGenerateCount` 分批传输；Studio 层 `BeginBatchModification` 跨批次共用一个 session，Preview 仅 patch 1 次 |
| stderr 日志 | 所有调试日志写 stderr，stdout 只输出 JSON，符合 MCP 规范 |

**Plugin 业务层双轨架构：** `KanziService.Sdk.cs` 封装 `Project`、`ProjectItem`、`PropertyContainer`、`BindingHost`、`Commands` 等官方 API，作为节点查询、属性读写、绑定、资源导入等操作的**首选路径**；`KanziService.Reflection.cs` 提供 Wrapper 解包与 Legacy 遍历兜底；`KanziService.Mutate.Legacy.cs` 隔离无 SDK 公开 API 的黑盒反射（如 `GetInternalProjectItem`）。

---

## 2026-06 重构成果摘要

本次重构建立了清晰的架构边界，将核心业务逻辑迁移至 SDK 优先路径，同时保留了必要的反射降级机制，显著提升了代码的可维护性和核心操作性能。

| 维度 | 成果 |
|------|------|
| **架构建立** | 新增 `Sdk.cs`（SDK 优先层）和 `Mutate.Legacy.cs`（永久黑盒区），明确了代码职责 |
| **代码精简** | 删除死代码、合并冗余逻辑，`Services` 目录净精简约 **843 行** |
| **质量提升** | 编译警告从 **53 个**降至 **35 个**；`QueryNodes`、`SetProperty`、`ImportImage` 等热路径已优先使用 SDK |
| **边界清晰** | `Reflection.cs`（通用反射工具）与 `Properties.cs`（复杂属性写入降级链）被明确定义为**永久兜底区**，防止未来误用 |
| **状态机性能** | `StateManager.cs` 接入 Studio **Batch Modification**（`BeginBatchModification` / `CommitBatchModification`，跨 MCP 批次 session）；**500 State ≈ 30+ s**（此前同规模约 **35–40 min**），Preview patch 从每命令 1 次降为 **1 次** |

### 状态机创建性能（Batch Modification）

Kanzi Studio 默认对每个 undoable 命令触发 Live Preview patch，大批量创建 State 会出现 O(N²) 耗时。本插件在 `StateManager.cs` 中通过 Logic Project 的 batch API 解决：

| 机制 | 说明 |
|------|------|
| **跨 MCP 批次 session** | `batchIndex=0` 开启 batch，`isLastBatch=true` 才 commit；中间 MCP 往返不触发 patch |
| **Undo 合并** | 整次创建合并为 **1 条** Undo 记录 |
| **UI 稳定** | batch 活跃时跳过 `PumpWpfMessages`，避免操作期间 UI 重入崩溃 |

**实测（Kanzi 3.9.10）：**

| 规模 | 优化前（估） | 优化后（实测） |
|------|-------------|----------------|
| 500 State | ~35–40 min | **~30+ s** |
| Preview patch 次数 | 数千次（每命令 1 次） | **1 次** |

> MCP 客户端须按 `batchIndex=0..N-1` **顺序连续**调用；中途失败会提交已创建部分并结束 session。

---

## 关键技术

| 领域 | 技术选型 | 说明 |
|------|----------|------|
| **MCP 协议** | JSON-RPC 2.0 over stdio | 协议版本 `2024-11-05`；支持 `initialize`、`tools/list`、`tools/call` |
| **MCP Server** | .NET 10 (C# 12) | 自包含发布 `win-x64`，AI 客户端 fork 启动，无需预装运行时 |
| **Kanzi 插件** | .NET Framework 4.8 (C# 7.3) | MEF `[Export]` 加载；WPF 侧边栏 `KanziMcpWindow` |
| **Kanzi 集成** | SDK 优先 + 反射兜底 + `PluginInterface.dll` | `Sdk.cs` 强类型调用为主；`Reflection.cs` / `Mutate.Legacy.cs` 多路 fallback |
| **进程间通信** | TCP localhost:9595 | 行分隔 JSON：`{method, args}` → `{result}` / `{error}` |
| **序列化** | `System.Text.Json` | Server 与 Plugin 均使用 camelCase；Plugin 侧手动 bundled DLL |
| **线程模型** | UI 线程调度 | Plugin 捕获 `SynchronizationContext`，Kanzi API 调用回到 Studio UI 线程 |
| **可靠性** | 懒连接 + 重试 + 超时 | 连接重试 2 次；请求断线重连；读超时 120s（Server）/ 600s（Plugin 大批量） |
| **远程方案** | 阿里云 OSS + Python | `kanzi_mcp_proxy.py` ↔ `oss_bridge_daemon.py`，非核心可选组件 |

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
│   │   └── ToolHandler.cs          # 20 个 MCP 工具定义与执行
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
│   │   └── KanziPipeServer.cs      # KanziTcpServer + 兼容别名 KanziPipeServer
│   ├── Services/
│   │   ├── KanziService.cs         # partial 主文件（Studio 注入、共享状态）
│   │   ├── KanziService.Sdk.cs             # SDK 优先层（Project / PropertyContainer 等）
│   │   ├── KanziService.Reflection.cs      # 反射兜底（Wrapper 解包、Legacy 遍历）
│   │   ├── KanziService.Mutate.Legacy.cs   # 永久黑盒区（无 SDK 公开 API 的反射）
│   │   ├── KanziService.Nodes.Query.cs     # 节点查询 / 搜索 / 树
│   │   ├── KanziService.Nodes.Mutate.cs    # 节点创建 / 删除
│   │   ├── KanziService.Properties.cs      # 属性读写 / 批量设置
│   │   ├── KanziService.CustomProperties.cs # CustomEnumProperty 创建/更新
│   │   ├── KanziService.StateManager.cs    # StateManager 分批创建 + Studio Batch Modification
│   │   ├── KanziService.Audit.cs           # 四类审计工具
│   │   ├── KanziService.Resources.cs       # 资源导入 / 诊断
│   │   ├── KanziService.Status.cs          # 连接与项目状态
│   │   ├── KanziService.Helpers.cs         # 序列化 / 日志 / 参数解析
│   │   ├── KanziApiDumper.cs               # 启动时 API 反射导出（调试）
│   │   └── Models/
│   │       ├── NodeFilter.cs
│   │       └── PropertyMetadata.cs
│   └── lib/                        # System.Text.Json 等依赖 DLL
│
├── kanzi_mcp_proxy.py              # OSS 远程 MCP 代理（可选）
├── oss_bridge_daemon.py            # Kanzi 机器 OSS 桥接守护进程（可选）
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
| 业务 | `KanziService.*.cs` | SDK 优先调用 Kanzi API，按领域拆分的 partial class；失败时反射兜底 |

### KanziService 模块划分

`KanziService` 采用 **partial class** 按职责拆分，新增功能时优先放入对应文件，避免单文件膨胀：

| 文件 | 职责 | 对应 MCP 工具 / Pipe method |
|------|------|-------------------------------|
| `Sdk.cs` | **SDK 优先调用层**。封装 Kanzi 官方 API（`Project`、`ProjectItem`、`PropertyContainer`、`BindingHost`、`Commands` 等）的强类型调用，是业务逻辑的**首选路径** | 被 `Properties.cs`、`Nodes.Mutate.cs`、`Resources.cs`、`Nodes.Query.cs`、`Audit.cs` 等模块调用 |
| `Mutate.Legacy.cs` | **永久黑盒隔离区**。集中存放无 SDK 公开 API、必须保留反射的方法（如 `GetInternalProjectItem`、`TryExecuteKanziPluginCommand`、`FindNodeType`），**禁止 SDK 化** | 被 `Nodes.Mutate.cs`、`Resources.cs`、`Sdk.cs` 等模块调用 |
| `Nodes.Query.cs` | 节点查询、树、搜索 | `query_nodes`, `get_node_tree`, `search_nodes`, `list_node_types` |
| `Nodes.Mutate.cs` | 节点 CRUD | `create_node`, `delete_node` |
| `Properties.cs` | 属性读写（SDK 优先 + 复杂属性反射降级链） | `set_property`, `batch_set_property`, `get_property_metadata` |
| `CustomProperties.cs` | CustomEnum 属性 | `upsert_custom_enum_property` |
| `StateManager.cs` | 状态机创建；MCP 分批 + Studio Batch session（500 State ≈ 30+ s） | `create_state_manager` |
| `Audit.cs` | 项目审计 | `audit_*` 系列 |
| `Resources.cs` | 资源导入/诊断 | `import_image`, `import_fbx`, `doctor_resource` |
| `Reflection.cs` | **反射兜底区**。Wrapper 解包、`SafeConvertValue`、`*LegacyReflection` 路径遍历等通用反射工具 | 被 `Sdk.cs` 及各业务模块在 SDK 失败时调用 |
| `Helpers.cs` | JSON 序列化、日志 | 被各模块内部调用 |

---

## 实现原理

### 1. MCP 握手流程

AI 客户端连接后会按 MCP 规范依次发送：

```
客户端                          KanziMcpServer
  │── initialize ──────────────►│  返回 protocolVersion、capabilities、instructions
  │── initialized ─────────────►│  确认
  │── tools/list ──────────────►│  返回 20 个 kanzi_* 工具定义（含 inputSchema）
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
   → 经 Sdk.cs 强类型访问项目/节点（失败时 Reflection.cs 兜底）

5. 返回 {"result":"{...节点 JSON...}"}
   → Server 包装为 MCP 格式:
   {"content":[{"type":"text","text":"..."}],"isError":false}
   → 写 stdout
```

### 3. TCP 协议格式（Server ↔ Plugin）

- **地址**：`127.0.0.1:9595`（可通过 `tcp:PORT` 或 `--pipe tcp:PORT` 修改）
- **格式**：每行一条 JSON，UTF-8 无 BOM，换行分隔（JSON 内禁止换行）
- **请求**：`{"method":"query_nodes","args":{...}}`
- **成功响应**：`{"result": ...}`
- **失败响应**：`{"error":"错误信息"}`

### 4. 可靠性机制

| 机制 | 位置 | 行为 |
|------|------|------|
| 懒连接 | `KanziPipeClient` | 启动时后台连 TCP，失败不阻塞；首次请求再连 |
| 连接重试 | `ConnectAsync` | 最多 2 次，间隔 2–3 秒 |
| 请求重试 | `SendRequestAsync` | 超时/断线后重连，指数退避 2s/4s |
| 读超时 | Server 120s / Plugin 600s | 复杂反射查询与大批量状态机创建 |
| UI 线程调度 | `KanziTcpServer` | 捕获 Studio UI 线程上下文，Kanzi API 在正确线程执行 |
| 日志轮转 | Plugin | `KanziMcpPlugin.log` 超过 1MB 自动截断 |

### 5. 反射策略（KanziService 核心）

**当前策略（双轨架构）：** 所有 Kanzi API 调用**优先**通过 `KanziService.Sdk.cs` 中的强类型 SDK 方法执行（如 `_studio.ActiveProject`、`Project.GetProjectItem`、`PropertyContainer.Set/Get`、`BindingHost.Bindings`、`Commands.ImportImages` 等）。仅在 SDK 路径不可用（如无公开 API、处理动态属性/跨程序集类型、非标准容器等）或明确失败时，才降级到 `Reflection.cs` 及 `Mutate.Legacy.cs` 中的反射逻辑作为兜底。这提升了核心路径的性能和可维护性。

**SDK 优先路径（热路径示例）：**

| 操作 | SDK 路径（`Sdk.cs`） | 反射兜底 |
|------|----------------------|----------|
| 获取当前项目 / 按路径找节点 | `ActiveProject` → `GetProjectItem(path)` | `*LegacyReflection` 多路查找 + 路径遍历 |
| 遍历子节点 | `ProjectItem.Children` | `GetChildrenLegacyReflection`（非标准容器强制反射） |
| 读取 / 写入属性 | `PropertyContainer.Get/Set` | `Properties.cs` 多策略链（Text/LocalizedString 永久走反射） |
| 读取绑定 / 修改 binding code | `BindingHost.Bindings` / `Binding.Code` | `GetBindingsInfoLegacyReflection` |
| 导入图片 / FBX | `Commands.ImportImages` / `CreateProjectItem<Asset3DSourceFile>` | `Resources.cs` legacy 链 + `Mutate.Legacy.cs` 黑盒 |

**反射兜底细节（`Reflection.cs` / `Mutate.Legacy.cs`）：**

| 操作 | 策略 |
|------|------|
| 获取当前项目（Legacy） | 5 路查找：`FlattenHierarchy` → 继承链 → 接口 → `Project` 属性 → 扫描 |
| 按路径找节点（Legacy） | 路径拆分 + `Children` 遍历 |
| 读取属性值（Wrapper 解包） | `DynamicProperty.Value` → 直接属性 → Indexer，共 5 策略 |
| 创建节点（Legacy） | SDK `CreateProjectItem<T>` 失败后，8 策略反射 fallback |
| PluginWrapper 解包 | `GetInternalProjectItem`（`Mutate.Legacy.cs`，永久黑盒） |
| 序列化 | `SafeSerialize` 处理 DBNull、循环引用、不可序列化类型 |

启动时 `KanziApiDumper` 会将 Studio 真实 API 面导出到 `C:\temp\KanziApiDump.txt`，便于调试新版本 Kanzi。

### 6. 日志位置

| 日志 | 路径 | 内容 |
|------|------|------|
| MCP Server | stderr | `[KanziMcpServer]`、`[KanziPipeClient]` |
| Kanzi 插件 | `C:\temp\KanziMcpPlugin.log` | 插件加载、TCP 连接、请求处理 |
| API Dump | `C:\temp\KanziApiDump.txt` | 启动时反射导出的 Kanzi API（调试用） |

---

## MCP 工具清单

共 **20 个工具**，均在 `ToolHandler.GetToolDefinitions()` 中注册。

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
| `kanzi_doctor_resource` | 诊断未使用的 Image/Texture，可选检测磁盘上缺失的纹理文件 |

### 自定义属性与状态机

| 工具名 | 说明 |
|--------|------|
| `kanzi_upsert_custom_enum_property` | 创建或更新 CustomEnumProperty（状态组控制器属性） |
| `kanzi_create_state_manager` | 创建 StateManager + StateGroup + States + StateObjects，支持分批 |

**状态机典型工作流：**

1. 先调用 `kanzi_upsert_custom_enum_property` 确保 `groupProperty` 存在
2. 用 `kanzi_create_state_manager` + `mode=preview` 查看分批计划
3. 大批量：`autoGenerateCount` + 1 个模板（字符串可用 `{0}` 占位），`batchSize=12~16`，**按顺序**循环 `batchIndex` + `mode=apply`（Studio 侧自动跨批次共用一个 batch session）
4. 超过 200 个 state 需设 `confirmLargeBatch=true`；单组上限 **500** State，过多拆多个 StateGroup
5. **性能参考**：500 State 全量创建约 **30+ 秒**；创建期间避免手动操作 Kanzi Studio UI

### 审计

| 工具名 | 说明 |
|--------|------|
| `kanzi_audit_bindings` | 审计/修改数据绑定（空 code、重复 code、属性解析失败；支持 preview/apply 修改 binding code） |
| `kanzi_audit_project_structure` | 审计命名规范、层级深度 |

### 已废弃工具（仍可通过名称调用，返回 compat 响应）

| 工具名 | 替代方案 |
|--------|----------|
| `kanzi_audit_localization` | 已下线；请用 `kanzi_search_nodes`（searchIn: Text）或 `kanzi_query_nodes` |
| `kanzi_audit_resource_references` | 转发至 `kanzi_doctor_resource`（响应含 `deprecated: true`） |

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
| Python 3 | 可选，用于 `test_mcp_client.py` 测试及 OSS 远程桥接 |

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

# 创建状态机（先建枚举属性，再 preview 分批计划）
用 kanzi_upsert_custom_enum_property 创建 PopState 枚举
用 kanzi_create_state_manager preview 查看分批方案
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

完整 MCP 工具测试用例（18 项，含自然语言指令、JSON 参数与验证点）见 **[MCP_Command_Use.md](MCP_Command_Use.md)**。

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
| 工具返回 timeout | 项目过大或 Kanzi 繁忙 | 增大超时；大批量状态机用分批；查 `KanziMcpPlugin.log` |
| MCP 客户端看不到 kanzi 工具 | `.mcp.json` 路径错误或 exe 不存在 | 检查 `command` 绝对路径；手动运行 exe 测 stderr |
| stdout 混入非 JSON | 误用 `Console.WriteLine` 打日志 | 日志必须写 stderr（代码已遵守） |
| 插件加载失败 | 缺少 `lib/*.dll` | 将 `src/KanziMcpPlugin/lib/` 下所有 DLL 复制到 plugins 目录 |
| `NETSDK1004` 找不到 assets 文件 | 删除了 `obj/` 但脚本用了 `--no-restore` | 使用最新 `publish.bat`（已含 restore 步骤），或手动 `dotnet restore` |
| 9595 端口被占用 | 其他程序占用 | 修改 Server/Plugin 端口（需改代码常量）或释放端口 |
| 状态机创建很慢 | 未按顺序调用 `batchIndex`，或 Studio batch session 中断 | 确保 `batchIndex=0..N-1` 连续调用；查日志 `BatchSession:` / `BeginStudioBatchModification`；500 State 正常应 **~30+ s** |
| 状态机创建卡住 / 崩溃 | 创建过程中手动点击 Studio UI | batch 期间勿操作 Studio；失败后可查 `C:\temp\KanziMcpPlugin.log` 中 `EnsureBatchClosed` |

---

## 扩展开发与可扩展性

### 添加新 MCP 工具（标准四步）

```
ToolHandler.cs          ← MCP 工具 Schema + 参数解析 + ExecuteXxxAsync
       ↓
KanziPipeClient.cs      ← SendRequestAsync 封装
       ↓ TCP
KanziPipeServer.cs      ← ProcessRequest switch 增加 method 分支
       ↓
KanziService.*.cs       ← 业务实现（放入对应 partial 文件）
```

**命名约定：**

| 层 | 命名 | 示例 |
|----|------|------|
| MCP 工具 | `kanzi_<verb>_<noun>` | `kanzi_query_nodes` |
| Pipe method | `snake_case` | `query_nodes` |
| 环境变量 | `KANZI_*` 前缀 | `KANZI_PIPE_NAME` |

**Schema 约定：**

- 可选参数用 `"default"` 而非放进 `required`
- 修改类工具默认 `mode: "preview"`，`enum: ["preview", "apply"]`
- 数组参数用 `"type": "array"` + `"items"`

### 扩展 KanziService 的建议

1. **优先新增 partial 文件**：如 `KanziService.Animations.cs`，而非继续膨胀单个文件
2. **SDK 优先，反射兜底**：新 API 访问先查 `KanziApiDump.txt`，优先在 `Sdk.cs` 添加强类型路径；仅无 SDK 或需 Wrapper 解包时在 `Reflection.cs` / `Mutate.Legacy.cs` 添加 fallback
3. **遵循 preview/apply 模式**：所有写操作先返回计划 JSON，apply 时再执行
4. **大批量操作考虑分批**：参考 `StateManager.cs` 的 `batchIndex` / `autoGenerateCount` 协议；写操作可复用 Studio `BeginBatchModification` 模式（见 `BatchSession`）
5. **UI 线程**：耗时操作在 Plugin 侧通过 `SynchronizationContext` 或 `Dispatcher` 调度

### 适配新 Kanzi 版本

1. 将对应版本的 `PluginInterface.dll` 放入 `pluginInterface/kanziX.Y.Z/`
2. 运行 `publish.bat X.Y.Z` 编译
3. 启动 Studio 后检查 `KanziApiDump.txt`，对比 API 变化
4. 按需在 `Sdk.cs` 增加 SDK 路径，或在 `Reflection.cs` / `Mutate.Legacy.cs` 增加 fallback 分支

### 后续可扩展方向

| 方向 | 说明 | 扩展入口 |
|------|------|----------|
| **MCP Resources** | 暴露项目快照、节点树为可读资源 | `McpProtocolHandler` 已有 `resources/list` 桩 |
| **MCP Prompts** | 预置 Kanzi 开发工作流 prompt 模板 | 新增 `PromptHandler` |
| **动画 / Timeline** | 操作 Animation Clip、Timeline | 新增 `KanziService.Animations.cs` |
| **Prefab / 模板** | 批量实例化 Prefab、应用模板 | `Nodes.Mutate.cs` 扩展 |
| **Undo / 事务** | 批量操作支持回滚 | Plugin 侧包装 Kanzi Undo API |
| **多项目 / 多 Studio** | 同时连接多个 Kanzi 实例 | 扩展 TCP 为多端口或实例 ID 路由 |
| **非 Windows 客户端** | AI 在 Mac/Linux，Kanzi 在 Windows | 已有 OSS 桥接方案，可演进为 WebSocket/gRPC |
| **工具粒度** | 按场景组合高频操作 | 在 `ToolHandler` 增加 composite 工具 |
| **配置化端口** | 运行时改端口无需改代码 | Plugin 读配置文件 / 环境变量 |

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

本地开发直接使用 TCP 直连即可，无需 OSS 组件。远程模式下 `REQUEST_TIMEOUT` 默认 660s，需大于 Plugin 单批 apply 超时（600s）。

---

## 许可证

MIT License
