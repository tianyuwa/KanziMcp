# Suggested Commands

## Build

```bash
# Build plugin (from repo root)
dotnet build src/KanziMcpPlugin/KanziMcpPlugin.csproj -c Release --no-restore

# Build server
dotnet build src/KanziMcpServer/KanziMcpServer.csproj -c Release

# Publish server (self-contained, win-x64)
dotnet publish src/KanziMcpServer/KanziMcpServer.csproj -c Release -r win-x64 --self-contained true

# Full build + package (Windows batch)
publish.bat               # default: Kanzi 3.9.10
publish.bat 3.9.9         # specific version
# Steps: clean → copy PluginInterface → build plugin → publish server → PyInstaller → assemble
```

## Run

```bash
# Start MCP server directly
dotnet run --project src/KanziMcpServer -- --verbose

# With custom pipe/timeout
dotnet run --project src/KanziMcpServer -- --pipe tcp:9595 --timeout 10000

# Claude Code registers server via .mcp.json or /mcp add
```

## Deploy

- Plugin DLL → `C:\ProgramData\Rightware\Kanzi\<version>\plugins\`
- Full output assembled at `Build_MCP/` by `publish.bat`

## Git

Standard git commands work. No special Windows quirks.
