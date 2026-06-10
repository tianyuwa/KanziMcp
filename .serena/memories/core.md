# Kanzi MCP Server — Core

Bridge that lets AI assistants (Claude Code) control Kanzi Studio via MCP protocol.

## Architecture (4 layers)

```
Claude Code ←─ JSON-RPC/stdio ─→ KanziMcpServer(.NET 10) ←─ TCP:9595 ─→ KanziMcpPlugin(.NET 4.8) ←─ reflection ─→ Kanzi Studio API
```

- **KanziMcpServer** (`src/KanziMcpServer`): .NET 10 console app, stdin/stdout JSON-RPC loop. `Program.cs` → `McpProtocolHandler` → `ToolHandler` → `KanziPipeClient`
- **KanziMcpPlugin** (`src/KanziMcpPlugin`): .NET Framework 4.8 class library loaded by Kanzi Studio via MEF. `KanziMcpPlugin.cs` → `KanziPipeServer` → `KanziService`
- Despite naming, uses **TCP on localhost:9595**, not Named Pipes (security context workaround)
- Dual deployment: local direct-TCP or remote via OSS proxy (`kanzi_mcp_proxy.py` + `oss_bridge_daemon.py`)
- Remote mode uses Alibaba Cloud OSS bucket as message queue

## Key files

- `src/KanziMcpServer/Program.cs` — entry, arg parsing, main loop (see `mem:server/core`)
- `src/KanziMcpServer/Handlers/McpProtocolHandler.cs` — JSON-RPC 2.0 routing
- `src/KanziMcpServer/Handlers/ToolHandler.cs` — 18 MCP tool definitions + dispatch
- `src/KanziMcpServer/Services/KanziPipeClient.cs` — TCP client to plugin
- `src/KanziMcpPlugin/KanziMcpPlugin.cs` — MEF plugin entry for Kanzi Studio
- `src/KanziMcpPlugin/PipeServer/KanziPipeServer.cs` — TCP listener inside Studio
- `src/KanziMcpPlugin/Services/KanziService.cs` — all business logic (~7000 lines), pure reflection

## Build output

`Build_MCP/` — assembled deployable: KanziMcpServer (self-contained .exe), KanziMcpPlugin DLLs + deps, main.exe (PyInstaller test client)

For more: tech stack → `mem:tech_stack`, build/deploy commands → `mem:suggested_commands`, conventions → `mem:conventions`, task completion checklist → `mem:task_completion`
