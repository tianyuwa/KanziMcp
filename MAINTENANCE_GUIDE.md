# KanziMcpServer 维护与扩展指南

> 最后更新: 2026-05-09
> 项目状态: MCP 全链路跑通，核心查询+属性写入+搜索+绑定查询已完成

---

## 一、项目架构总览

```
┌─────────────────────────────────────────────────────────┐
│  MCP Client (Cursor/Claude Desktop/测试脚本)            │
│  ↕ stdio (JSON-RPC 2.0)                                │
├─────────────────────────────────────────────────────────┤
│  KanziMcpServer.exe (独立进程, .NET 10)                 │
│  ┌─ Program.cs              入口，读 stdin 写 stdout    │
│  ├─ McpProtocolHandler.cs   JSON-RPC 协议解析/路由       │
│  ├─ ToolHandler.cs          MCP 工具定义与参数转换         │
│  ├─ KanziPipeClient.cs      Named Pipe 客户端            │
│  └─ Models/                 数据模型                     │
│  ↕ Named Pipe: KanziMcpPipe                            │
├─────────────────────────────────────────────────────────┤
│  KanziMcpPlugin.dll (Kanzi Studio 进程内, .NET 4.8)     │
│  ┌─ KanziMcpPlugin.cs       插件入口（MEF [Export]）     │
│  ├─ KanziMcpWindow.cs       插件面板窗口                 │
│  ├─ KanziPipeServer.cs      Named Pipe 服务端            │
│  ├─ KanziService.cs         Kanzi API 业务逻辑（核心!）  │
│  └─ KanziApiDumper.cs       API 反射导出（调试用）       │
└─────────────────────────────────────────────────────────┘
```

---

## 二、源文件清单与职责

### KanziMcpPlugin 项目（.NET 4.8，运行在 Kanzi Studio 进程内）

| 文件 | 职责 | 修改频率 |
|------|------|----------|
| `KanziMcpPlugin.cs` | 插件入口，MEF 加载，启动 PipeServer | 极少 |
| `KanziMcpWindow.cs` | 插件面板窗口 UI | 极少 |
| `PipeServer/KanziPipeServer.cs` | Named Pipe 服务端，接收请求并路由到 KanziService | 偶尔（协议变更时） |
| `Services/KanziService.cs` | **核心业务层**，所有 Kanzi API 交互逻辑 | **最频繁** |
| `Services/KanziApiDumper.cs` | 反射导出 API 帮助调试 | 极少（仅调试时用） |

### KanziMcpServer 项目（.NET 10，独立进程）

| 文件 | 职责 | 修改频率 |
|------|------|----------|
| `Program.cs` | 入口，读 stdin 写 stdout | 极少 |
| `Handlers/McpProtocolHandler.cs` | JSON-RPC 协议解析 | 极少 |
| `Handlers/ToolHandler.cs` | **工具定义与参数转换** | **频繁**（新增/修改工具时） |
| `Services/KanziPipeClient.cs` | Named Pipe 客户端，封装管道通信 | 偶尔（新增工具方法时） |
| `Models/JsonRpcModels.cs` | JSON-RPC 数据模型 | 极少 |
| `Models/NodeModels.cs` | 节点查询数据模型 | 偶尔（新增查询参数时） |

---

## 三、后续功能扩展 — 需要修改的文件

### 场景 A: 新增一个 MCP 工具（如 `kanzi_create_node`）

**必须修改的 3 个文件：**

1. **`KanziService.cs`** — 添加业务方法
   ```csharp
   // 在 KanziService 类中添加新方法
   public string CreateNode(JsonElement? args)
   {
       // 解析参数
       // 反射调用 Kanzi API
       // 返回 JSON 结果
   }
   ```

2. **`KanziPipeServer.cs`** — 注册新方法路由
   ```csharp
   // 在 ProcessRequest 的 switch/method 路由中添加
   case "create_node":
       result = _kanziService.CreateNode(args);
       break;
   ```

3. **`ToolHandler.cs`** — 定义工具 schema + 实现执行方法
   ```csharp
   // 1. 在 GetToolDefinitions() 中添加工具定义
   new() { Name = "kanzi_create_node", ... }

   // 2. 添加工具 schema 方法
   private static ToolDefinition GetCreateNodeTool() => new() { ... }

   // 3. 添加执行方法
   private async Task<string> ExecuteCreateNodeAsync(JsonElement args) { ... }

   // 4. 在 ExecuteToolAsync 的 switch 中注册
   case "kanzi_create_node": return await ExecuteCreateNodeAsync(args);
   ```

4. **`KanziPipeClient.cs`** — 添加客户端封装方法（可选但推荐）
   ```csharp
   public async Task<string> CreateNodeAsync(string path, string type, string? name) { ... }
   ```

### 场景 B: 修改现有工具行为（如 search_nodes 增加过滤条件）

1. **`KanziService.cs`** — 修改 SearchNodes 方法逻辑
2. **`ToolHandler.cs`** — 修改 GetSearchNodesTool() 的 InputSchema 和 ExecuteSearchNodesAsync() 的参数处理
3. **`KanziPipeClient.cs`** — 修改 SearchNodesAsync() 的参数传递（如有新增参数）

### 场景 C: 适配新版本 Kanzi（如 3.9.11）

1. **`pluginInterface/`** — 放入新版本的 PluginInterface.dll
2. **`KanziService.cs`** — 可能需要调整反射路径（检查新版 API 是否有变化）
3. **`KanziApiDumper.cs`** — 在新版 Kanzi Studio 中重新导出 API dump，对比差异
4. **`KanziMcpPlugin.csproj`** — 检查是否需要新增依赖 DLL

---

## 四、编译与部署步骤

### 编译 Plugin（需要对应版本的 PluginInterface.dll）

```bash
# 1. 复制 PluginInterface.dll
cp pluginInterface/kanzi3.9.10/PluginInterface.dll ./PluginInterface.dll

# 2. 编译（--no-restore 绕过 .NET SDK 10.0.203 NuGet bug）
dotnet build src/KanziMcpPlugin/KanziMcpPlugin.csproj -c Release --no-restore

# 3. 产物在
ls src/KanziMcpPlugin/bin/Release/net48/PluginKanziMCP.dll
```

### 编译 Server（.NET 10 自包含发布）

```bash
dotnet build src/KanziMcpServer/KanziMcpServer.csproj -c Release --no-restore

# 发布自包含版本
dotnet publish src/KanziMcpServer/KanziMcpServer.csproj -c Release -r win-x64 \
    --self-contained true --no-restore -o publish2/KanziMcpServer
```

### 打包测试客户端

```bash
python -m PyInstaller --onefile --name main --distpath publish2/ test_mcp_client.py
```

### 部署到 Kanzi 机器

```
# 只需拷贝一个 DLL 到 plugins 目录！
C:\ProgramData\Rightware\Kanzi 3.9.10\plugins\PluginKanziMCP.dll

# 注意：不要放其他依赖 DLL 到 plugins 目录！会导致 Kanzi Studio 卡死！
```

---

## 五、关键技术陷阱（踩坑记录）

| # | 陷阱 | 解决方案 |
|---|------|----------|
| 1 | PluginInterface.dll 不能放 plugins 目录 | 只放 PluginKanziMCP.dll，其他依赖让 CLR 从 Kanzi 安装目录加载 |
| 2 | .NET SDK 10.0.203 NuGet restore 报错 | 用 `--no-restore` + 预先准备好的 project.assets.json |
| 3 | Named Pipe 必须用 Byte 模式 | Message 模式与 StreamReader 缓冲区不兼容 |
| 4 | StreamWriter 必须用 UTF8Encoding(false) | Encoding.UTF8 默认带 BOM，会导致 JSON 解析失败 |
| 5 | CreateNamedPipeW maxInstances | 必须硬编码 255，不能用 MaxAllowedServerInstances(-1) |
| 6 | Pipe 响应不能双重序列化 | 用 `$"{{\"result\":{resultJson}}}"` 直接嵌入 JSON |
| 7 | MEF 加载下 Assembly.Location 返回空 | 用 CodeBase → CommonApplicationData 回退 |
| 8 | Kanzi Studio 吞掉 Initialize 异常 | 必须在 Initialize 中 try-catch 包裹 |
| 9 | 反射必须用 FlattenHierarchy | 否则找不到继承链上的属性/方法 |
| 10 | DynamicProperty.Value 可能读取失败 | 用 TryReadPropertyValue 多策略尝试 |
| 11 | 节点重命名后路径变化 | apply 修改 Name 后旧路径失效，需用新路径 |
| 12 | searchIn 默认值需双层统一 | ToolHandler 和 KanziService 默认值必须一致 |

---

## 六、调试技巧

1. **查看插件日志**: `C:\temp\KanziMcpPlugin.log`
2. **导出 API dump**: 触发 `KanziApiDumper.DumpApi()`，查看 `C:\temp\KanziApiDump.txt`
3. **测试客户端**: 运行 `main.exe --server "path\to\KanziMcpServer.exe"`
4. **详细日志**: 运行 `KanziMcpServer.exe --verbose`（日志输出到 stderr）
5. **手动测试**: 在测试客户端中用 `raw {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{...}}`

---

## 七、项目完成度评估

| 功能 | 状态 | 完成度 |
|------|------|--------|
| MCP 通信通道（stdio + Named Pipe） | ✅ 已完成 | 100% |
| 节点查询（query_nodes / get_node_tree） | ✅ 已完成 | 100% |
| 节点搜索（search_nodes） | ✅ 已完成 | 100% |
| 属性写入（set_node_property） | ✅ 已完成 | 100% |
| 属性读取（includeProperties） | 🔄 部分完成 | 70% |
| 绑定查询（get_binding_info） | ✅ 已完成 | 100% |
| 节点类型列表（list_node_types） | ✅ 已完成 | 100% |
| 批量属性修改（batch_set_property） | 🔲 框架已有 | 50% |
| 属性元数据（get_property_metadata） | 🔲 框架已有 | 40% |
| 审计工具×3（bindings/localization/structure） | 🔲 框架已有 | 30% |
| Kanzi 版本自适应（3.9.6/3.9.9） | 🔲 未开始 | 0% |

**总体完成度: ~70%**
- 核心功能（查询/搜索/属性写入/绑定）已全部可用
- 扩展功能（批量修改/审计/多版本适配）框架已搭好，填充业务逻辑即可
