# Kanzi MCP Server

通过 Model Context Protocol (MCP) 让 AI 助手（如 Claude Code）能够与 Kanzi Studio 项目交互。

## 架构

```
┌─────────────────┐     JSON-RPC/stdio     ┌─────────────────┐
│                 │  ←──────────────────→  │                 │
│   Claude Code   │                        │   MCP Server    │
│                 │                        │  (独立进程)     │
└─────────────────┘                        └────────┬────────┘
                                                    │
                                                    │ Named Pipe
                                                    ↓
                                             ┌─────────────────┐
                                             │  Kanzi 插件     │
                                             │ (嵌入 Studio)   │
                                             └────────┬────────┘
                                                      │
                                                      ↓
                                             ┌─────────────────┐
                                             │ Kanzi Engine    │
                                             │ PluginInterface │
                                             └─────────────────┘
```

## 功能

### 查询工具
- `kanzi_query_nodes` - 按类型/名称/路径查询节点
- `kanzi_get_node_tree` - 获取节点树结构
- `kanzi_list_node_types` - 列出所有节点类型
- `kanzi_search_nodes` - 搜索节点内容

### 属性操作工具
- `kanzi_set_node_property` - 设置单个节点属性
- `kanzi_batch_set_property` - 批量设置节点属性
- `kanzi_get_property_metadata` - 获取属性元数据

### 审计工具
- `kanzi_audit_bindings` - 审计数据绑定完整性
- `kanzi_audit_localization` - 审计多语言覆盖
- `kanzi_audit_project_structure` - 审计项目结构

## 安装

### 1. 编译项目

```bash
# 编译 MCP Server
cd src/KanziMcpServer
dotnet build -c Release

# 编译 Kanzi 插件
cd ../KanziMcpPlugin
dotnet build -c Release
```

### 2. 部署插件

将 `src/KanziMcpPlugin/bin/Release/PluginKanziMCP.dll` 复制到 Kanzi 插件目录：

```
C:\ProgramData\Rightware\Kanzi\<版本>\plugins\
```

### 3. 配置 Claude Code

在项目根目录创建 `.mcp.json`：

```json
{
  "mcpServers": {
    "kanzi": {
      "command": "path/to/KanziMcpServer.exe",
      "args": [],
      "env": {},
      "timeout": 5000
    }
  }
}
```

或在 Claude Code 中运行：
```
/mcp add kanzi "path/to/KanziMcpServer.exe"
```

## 使用

### 启动 Kanzi Studio

1. 确保 KanziMCP 插件已加载
2. 打开一个 Kanzi 项目
3. 插件会自动启动 Named Pipe Server

### 在 Claude Code 中使用

```
# 查询所有 TextBlock2D 节点
用 kanzi_query_nodes 查找所有文本节点

# 批量修改属性
用 kanzi_batch_set_property 把所有警告文字改成红色

# 审计绑定
用 kanzi_audit_bindings 检查项目的数据绑定
```

## 命令行参数

```
KanziMcpServer [选项]

选项:
  --help, -h          显示帮助
  --version           显示版本
  --pipe, -p <名称>   Named Pipe 名称 (默认: KanziMcpPipe)
  --timeout, -t <ms>  连接超时 (毫秒)
  --verbose, -v       详细输出
  --no-auto-connect   不自动连接
```

## 开发

### 项目结构

```
kanziMcpServer/
├── src/
│   ├── KanziMcpServer/          # MCP Server (独立进程)
│   │   ├── Handlers/            # 协议和工具处理
│   │   ├── Models/              # 数据模型
│   │   ├── Services/            # Named Pipe 客户端
│   │   └── Program.cs           # 入口
│   │
│   └── KanziMcpPlugin/          # Kanzi 插件
│       ├── PipeServer/          # Named Pipe 服务端
│       ├── Services/            # Kanzi 服务
│       └── KanziMcpPlugin.cs    # 插件入口
│
├── tests/
│   └── KanziMcpServer.Tests/    # 单元测试
│
├── docs/                        # 文档
└── scripts/                     # 脚本
```

### 添加新工具

1. 在 `KanziMcpServer/Handlers/ToolHandler.cs` 中添加工具定义
2. 在 `KanziMcpPlugin/Services/KanziService.cs` 中添加实现
3. 更新 `docs/API_SPEC.md`

## 许可证

MIT License
