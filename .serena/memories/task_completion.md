# Task Completion Checklist

When a coding task is considered done, run these steps:

## For KanziMcpServer (.NET 10) changes

```bash
# Type check / build
dotnet build src/KanziMcpServer/KanziMcpServer.csproj -c Release
```

## For KanziMcpPlugin (.NET 4.8) changes

```bash
# Build with --no-restore (restore handled separately due to NuGet path issues)
dotnet build src/KanziMcpPlugin/KanziMcpPlugin.csproj -c Release --no-restore
```

## For Python script changes

```bash
# Basic syntax check
python -m py_compile <script>.py
```

## Integration test (manual)

1. Start Kanzi Studio with plugin loaded and a project open
2. Run `KanziMcpServer --verbose --no-auto-connect` (or wait for auto-connect)
3. Verify `kanzi_get_status` returns connected
4. Smoke test: `kanzi_list_node_types`, `kanzi_query_nodes` with common type

No automated test suite exists in this repo. Testing is manual via `test_mcp_client.py` / `test_mcp_client_latest.py`.
