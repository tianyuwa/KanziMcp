// JsonRpcModels.cs
//
// 文件作用: JSON-RPC 2.0 协议数据模型
// 关键类: JsonRpcRequest, JsonRpcResponse, JsonRpcError
// 主要职责:
//   1. 定义 MCP 协议的请求/响应/错误 数据模型
//   2. JsonRpcRequest  : 客户端请求（jsonrpc/method/params/id）
//   3. JsonRpcResponse : 成功响应（result + id）
//   4. JsonRpcError    : 错误响应（error.code + error.message）
//   5. ToolDefinition   : MCP 工具定义（name/description/inputSchema）
// 依赖: System.Text.Json（.NET 10 内置）
// 说明: 使用 [JsonPropertyName] 特性控制 JSON 序列化字段名

using System.Text.Json;
using System.Text.Json.Serialization;

namespace KanziMcpServer.Models;

/// <summary>
/// JSON-RPC 2.0 请求
/// </summary>
public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 响应
/// </summary>
public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse CreateSuccess(int id, object? result)
    {
        return new JsonRpcResponse { Id = id, Result = result };
    }

    public static JsonRpcResponse CreateError(int id, int code, string message, object? data = null)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Error = new JsonRpcError { Code = code, Message = message, Data = data }
        };
    }
}

/// <summary>
/// JSON-RPC 2.0 错误
/// </summary>
public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// MCP 协议常量
/// </summary>
public static class McpConstants
{
    public const string ProtocolVersion = "2024-11-05";
    public const string ServerName = "kanzi-mcp";
    public const string ServerVersion = "1.0.0";

    // Named Pipe 配置
    public const string DefaultPipeName = "KanziMcpPipe";
    public const int PipeConnectTimeout = 5000;   // 5 seconds per attempt (不阻塞测试超时)
    public const int PipeReadTimeout = 30000;     // 30 seconds for complex queries
    public const int PipeMaxRetries = 2;          // Maximum connection retry attempts

    // 错误码
    public const int ErrorParseError = -32700;
    public const int ErrorInvalidRequest = -32600;
    public const int ErrorMethodNotFound = -32601;
    public const int ErrorInvalidParams = -32602;
    public const int ErrorInternalError = -32603;

    // 业务错误码
    public const int ErrorNodeNotFound = -32001;
    public const int ErrorInvalidProperty = -32002;
    public const int ErrorPropertyReadOnly = -32003;
    public const int ErrorBatchPartialFailure = -32004;
    public const int ErrorKanziNotConnected = -32005;
    public const int ErrorKanziOperationFailed = -32006;
}
