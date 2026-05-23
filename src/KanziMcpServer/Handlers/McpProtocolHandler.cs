// McpProtocolHandler.cs
//
// 文件作用: JSON-RPC 2.0 协议处理器（MCP Server 入口）
// 关键类: McpProtocolHandler
// 主要职责:
//   1. 解析 stdin 收到的 JSON-RPC 请求（method: initialize/tools/list/tools/call）
//   2. 路由到 ToolHandler 执行具体工具逻辑
//   3. 构造符合 MCP 协议的响应（包含 content 数组）
//   4. 错误处理：将异常转为 JSON-RPC error 格式返回给客户端
// 协议说明:
//   - 请求格式: {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"...","arguments":...}}
//   - 响应格式: {"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"..."}],"isError":false}}
// 依赖: ToolHandler（工具路由），KanziPipeClient（管道通信）

using System.Text.Json;
using KanziMcpServer.Models;
using KanziMcpServer.Services;

namespace KanziMcpServer.Handlers;

/// <summary>
/// MCP 协议处理器 - 处理 JSON-RPC 请求
/// </summary>
public class McpProtocolHandler
{
    private readonly ToolHandler _toolHandler;
    private readonly KanziPipeClient _pipeClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public McpProtocolHandler(ToolHandler toolHandler, KanziPipeClient pipeClient)
    {
        _toolHandler = toolHandler;
        _pipeClient = pipeClient;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// 处理 JSON-RPC 请求
    /// </summary>
    public async Task<string> HandleRequestAsync(string input)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(input, _jsonOptions);
        }
        catch (JsonException ex)
        {
            return SerializeResponse(JsonRpcResponse.CreateError(
                0,
                McpConstants.ErrorParseError,
                $"JSON 解析错误: {ex.Message}"
            ));
        }

        if (request == null)
        {
            return SerializeResponse(JsonRpcResponse.CreateError(
                0,
                McpConstants.ErrorInvalidRequest,
                "无效的请求"
            ));
        }

        try
        {
            var result = request.Method switch
            {
                // MCP 协议方法
                "initialize" => await HandleInitializeAsync(request),
                "initialized" => HandleInitialized(request),
                "tools/list" => await HandleToolsListAsync(request),
                "tools/call" => await HandleToolsCallAsync(request),
                "resources/list" => HandleResourcesList(request),
                "resources/templates/list" => HandleResourceTemplatesList(request),

                // 自定义 Kanzi 方法
                "kanzi/status" => HandleKanziStatus(request),
                "kanzi/query_nodes" => await HandleQueryNodesAsync(request),
                "kanzi/get_node_tree" => await HandleGetNodeTreeAsync(request),
                "kanzi/list_node_types" => await HandleListNodeTypesAsync(request),
                "kanzi/set_property" => await HandleSetPropertyAsync(request),
                "kanzi/batch_set_property" => await HandleBatchSetPropertyAsync(request),

                _ => throw new NotSupportedException($"不支持的方法: {request.Method}")
            };

            return SerializeResponse(JsonRpcResponse.CreateSuccess(request.Id, result));
        }
        catch (NotSupportedException ex)
        {
            return SerializeResponse(JsonRpcResponse.CreateError(
                request.Id,
                McpConstants.ErrorMethodNotFound,
                ex.Message
            ));
        }
        catch (ArgumentException ex)
        {
            return SerializeResponse(JsonRpcResponse.CreateError(
                request.Id,
                McpConstants.ErrorInvalidParams,
                ex.Message
            ));
        }
        catch (Exception ex)
        {
            return SerializeResponse(JsonRpcResponse.CreateError(
                request.Id,
                McpConstants.ErrorInternalError,
                $"内部错误: {ex.Message}"
            ));
        }
    }

    #region MCP 协议方法

    private Task<object> HandleInitializeAsync(JsonRpcRequest request)
    {
        return Task.FromResult<object>(new
        {
            protocolVersion = McpConstants.ProtocolVersion,
            serverInfo = new
            {
                name = McpConstants.ServerName,
                version = McpConstants.ServerVersion,
                description = "Kanzi Studio MCP Server - AI-powered automation"
            },
            capabilities = new
            {
                tools = new { },
                resources = new { },
                prompts = new { }
            },
            instructions = "Kanzi MCP Server allows AI assistants to query and modify Kanzi Studio projects. " +
                           "Use kanzi_query_nodes to find nodes, kanzi_set_property to modify properties, " +
                           "and kanzi_batch_set_property for bulk operations."
        });
    }

    private object HandleInitialized(JsonRpcRequest request)
    {
        // 客户端初始化完成确认
        return new { };
    }

    private Task<object> HandleToolsListAsync(JsonRpcRequest request)
    {
        var tools = _toolHandler.GetToolDefinitions();
        return Task.FromResult<object>(new
        {
            tools = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                inputSchema = t.InputSchema
            })
        });
    }

    private async Task<object> HandleToolsCallAsync(JsonRpcRequest request)
    {
        if (request.Params == null)
            throw new ArgumentException("缺少参数");

        var args = request.Params.Value;

        if (!args.TryGetProperty("name", out var nameEl))
            throw new ArgumentException("缺少工具名称");

        var toolName = nameEl.GetString() ?? "";
        var toolArgs = args.TryGetProperty("arguments", out var argsEl) ? argsEl : default;

        var resultJson = await _toolHandler.ExecuteToolAsync(toolName, toolArgs);

        // ExecuteToolAsync 返回的是 JSON 字符串（由 KanziPipeClient 返回）
        // 需要把它解析为 JsonElement，再嵌入响应，避免双重 JSON 编码
        using var resultDoc = JsonDocument.Parse(resultJson);
        var resultElement = resultDoc.RootElement;

        var isError = resultElement.ValueKind == JsonValueKind.Object
                      && resultElement.TryGetProperty("error", out _);

        return new
        {
            content = new[]
            {
                new { type = "text", text = resultElement.GetRawText() }
            },
            isError
        };
    }

    private object HandleResourcesList(JsonRpcRequest request)
    {
        // 目前不支持资源订阅
        return new
        {
            resources = Array.Empty<object>()
        };
    }

    private object HandleResourceTemplatesList(JsonRpcRequest request)
    {
        // 目前不支持资源模板
        return new
        {
            resourceTemplates = Array.Empty<object>()
        };
    }

    #endregion

    #region Kanzi 自定义方法

    private object HandleKanziStatus(JsonRpcRequest request)
    {
        var status = new ServerStatus
        {
            KanziConnected = _pipeClient.IsConnected,
            ProjectOpen = _pipeClient.IsConnected, // TODO: 实际检查项目状态
            Uptime = DateTime.UtcNow - _pipeClient.ConnectedAt
        };

        return status;
    }

    private async Task<object> HandleQueryNodesAsync(JsonRpcRequest request)
    {
        var filter = ParseNodeQueryFilter(request.Params);
        var result = await _pipeClient.QueryNodesAsync(filter);
        return result;
    }

    private async Task<object> HandleGetNodeTreeAsync(JsonRpcRequest request)
    {
        string? rootPath = null;
        int depth = 3;

        if (request.Params.HasValue)
        {
            if (request.Params.Value.TryGetProperty("rootPath", out var pathEl))
                rootPath = pathEl.GetString();
            if (request.Params.Value.TryGetProperty("depth", out var depthEl))
                depth = depthEl.GetInt32();
        }

        return await _pipeClient.GetNodeTreeAsync(rootPath, depth);
    }

    private async Task<object> HandleListNodeTypesAsync(JsonRpcRequest request)
    {
        return await _pipeClient.ListNodeTypesAsync();
    }

    private async Task<object> HandleSetPropertyAsync(JsonRpcRequest request)
    {
        if (!request.Params.HasValue)
            throw new ArgumentException("缺少参数");

        var args = request.Params.Value;

        var path = args.TryGetProperty("path", out var p) ? p.GetString() : "";
        var property = args.TryGetProperty("property", out var pr) ? pr.GetString() : "";
        var value = args.TryGetProperty("value", out var v) ? v : default;
        var mode = args.TryGetProperty("mode", out var m) ? m.GetString() ?? "preview" : "preview";
        var force = args.TryGetProperty("force", out var f) && f.GetBoolean();

        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(property))
            throw new ArgumentException("缺少 path 或 property 参数");

        return await _pipeClient.SetPropertyAsync(path, property, value, mode, force);
    }

    private async Task<object> HandleBatchSetPropertyAsync(JsonRpcRequest request)
    {
        if (!request.Params.HasValue)
            throw new ArgumentException("缺少参数");

        var args = request.Params.Value;
        var filter = ParseNodeQueryFilter(args.TryGetProperty("filter", out var f) ? f : default);
        var properties = ParseProperties(args.TryGetProperty("properties", out var p) ? p : default);
        var mode = args.TryGetProperty("mode", out var m) ? m.GetString() ?? "preview" : "preview";
        var ignoreReadOnly = args.TryGetProperty("ignoreReadOnly", out var i) && i.GetBoolean();

        return await _pipeClient.BatchSetPropertyAsync(filter, properties, mode, ignoreReadOnly);
    }

    #endregion

    #region 辅助方法

    private NodeQueryFilter ParseNodeQueryFilter(JsonElement? element)
    {
        var filter = new NodeQueryFilter();

        if (!element.HasValue)
            return filter;

        var e = element.Value;

        if (e.TryGetProperty("type", out var typeEl))
            filter.Type = typeEl.GetString();
        if (e.TryGetProperty("name", out var nameEl))
            filter.Name = nameEl.GetString();
        if (e.TryGetProperty("path", out var pathEl))
            filter.Path = pathEl.GetString();
        if (e.TryGetProperty("includeProperties", out var incPropsEl))
            filter.IncludeProperties = incPropsEl.GetBoolean();
        if (e.TryGetProperty("includeBindings", out var incBindingsEl))
            filter.IncludeBindings = incBindingsEl.GetBoolean();
        if (e.TryGetProperty("recursive", out var recursiveEl))
            filter.Recursive = recursiveEl.GetBoolean();
        if (e.TryGetProperty("maxDepth", out var maxDepthEl))
            filter.MaxDepth = maxDepthEl.GetInt32();
        if (e.TryGetProperty("limit", out var limitEl))
            filter.Limit = limitEl.GetInt32();

        return filter;
    }

    private Dictionary<string, PropertyValue> ParseProperties(JsonElement? element)
    {
        var properties = new Dictionary<string, PropertyValue>();

        if (!element.HasValue)
            return properties;

        foreach (var prop in element.Value.EnumerateObject())
        {
            properties[prop.Name] = ParsePropertyValue(prop.Value);
        }

        return properties;
    }

    private PropertyValue ParsePropertyValue(JsonElement element)
    {
        var value = new PropertyValue();

        // 颜色 { r, g, b, a }
        if (element.TryGetProperty("r", out _))
        {
            value.Type = "color";
            if (element.TryGetProperty("r", out var rEl)) value.R = rEl.GetSingle();
            if (element.TryGetProperty("g", out var gEl)) value.G = gEl.GetSingle();
            if (element.TryGetProperty("b", out var bEl)) value.B = bEl.GetSingle();
            if (element.TryGetProperty("a", out var aEl)) value.A = aEl.GetSingle();
            return value;
        }

        // 向量 { x, y, z, w }
        if (element.TryGetProperty("x", out _))
        {
            value.Type = "vector";
            if (element.TryGetProperty("x", out var xEl)) value.X = xEl.GetSingle();
            if (element.TryGetProperty("y", out var yEl)) value.Y = yEl.GetSingle();
            if (element.TryGetProperty("z", out var zEl)) value.Z = zEl.GetSingle();
            if (element.TryGetProperty("w", out var wEl)) value.W = wEl.GetSingle();
            return value;
        }

        // 简单值
        value.Value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.ToString(),
            _ => element.ToString()
        };
        value.Type = element.ValueKind.ToString().ToLower();

        return value;
    }

    private string SerializeResponse(JsonRpcResponse response)
    {
        return JsonSerializer.Serialize(response, _jsonOptions);
    }

    #endregion
}
