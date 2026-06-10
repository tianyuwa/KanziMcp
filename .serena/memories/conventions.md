# Conventions

## Reflection patterns (KanziService)
- **All Kanzi API access is done via reflection** — no direct type references
- Every operation has multiple fallback strategies with try-catch (e.g., 5 ways to get ActiveProject, 4 ways to create a node)
- Use `BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static` for all lookups
- Property reading: `TryReadPropertyValue` with fallback chain (DynamicProperty.Value → direct property → indexer)
- Custom `SafeSerialize()` handles: DBNull, cyclic refs, non-serializable types, nested DynamicPropertyValue

## Error handling
- Server: `ArgumentException` for missing params, re-thrown as JSON-RPC `InvalidParams` (-32602)
- Plugin: methods return `{result: ...}` or `{error: "..."}`; pipe server catches exceptions and wraps as error response
- `KanziPipeClient.SendRequestAsync<T>` detects `{error: ...}` and throws `InvalidOperationException`

## IPC envelope format
- Request: `{"method": "query_nodes", "args": {...}}`
- Success: `{"result": <json>}`
- Error: `{"error": "<message>"}`
- Line-delimited (one JSON object per line, no internal newlines)

## MCP tool input schemas
- All optional parameters use `"default": <value>` in schema (not `"required"` array for them)
- Arrays use `"type": "array"` with `"items"` specification
- Preview/apply tools use `"enum": ["preview", "apply"]` with `"default": "preview"`

## Naming
- MCP tools: `kanzi_<verb>_<noun>` (e.g., `kanzi_query_nodes`, `kanzi_set_node_property`)
- Pipe methods: `camelCase` matching tool name suffix (e.g., `query_nodes`, `set_property`)
- Config: env vars with `KANZI_` prefix (e.g., `KANZI_PIPE_NAME`, `KANZI_CONNECT_TIMEOUT`)
- OSS proxy: uppercase with `OSS_` prefix
