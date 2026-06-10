# Tech Stack

## KanziMcpServer (MCP server process)
- **Runtime**: .NET 10 (`net10.0`), self-contained publish for win-x64/win-x86
- **Language**: C# 12
- **Serialization**: `System.Text.Json` (server side, stdio JSON-RPC)
- **Build**: `dotnet publish -r win-x64 --self-contained true`

## KanziMcpPlugin (Kanzi Studio plugin)
- **Runtime**: .NET Framework 4.8 (`net48`), class library → DLL loaded by Kanzi Studio
- **Language**: C# 7.3 (constrained by .NET Framework 4.8)
- **UI**: WPF (`KanziMcpWindow.xaml`/`.cs`) — plugin panel inside Kanzi Studio
- **Plugin API**: `PluginInterface.dll` — referenced manually (not NuGet), versioned per Kanzi release in `pluginInterface/kanzi<version>/`
- **Plugin framework**: MEF (`System.ComponentModel.Composition`), exports `PluginContent` with `PluginWindowFactory`
- **Dependencies**: manually bundled DLLs in `lib/` (System.Text.Json, System.Buffers, etc.)
- **Build**: `dotnet build --no-restore`

## Python scripts (auxiliary)
- **Runtime**: Python 3.12 (`C:\Users\WTY\AppData\Local\Programs\Python\Python312`)
- **Packaging**: PyInstaller (specs: `main.spec`, `oss_bridge_daemon.spec`)
- **OSS SDK**: `oss2` (Alibaba Cloud OSS Python SDK)

## Shared
- **Protocol**: JSON-RPC 2.0 over line-delimited stdio (MCP spec `2024-11-05`)
- **Inter-process**: TCP on `localhost:9595` (serialized JSON with `{method, args}` / `{result, error}` envelope)
- **Kanzi interaction**: 100% reflection — no compile-time dependency on Kanzi SDK beyond `PluginInterface.dll`

## Key versions
- Kanzi Studio default target: `kanzi3.9.10`
- MCP protocol: `2024-11-05`
- Server identity: `kanzi-mcp` v1.0.0
