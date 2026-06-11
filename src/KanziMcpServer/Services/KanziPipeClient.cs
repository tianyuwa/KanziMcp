// KanziPipeClient.cs
//
// 文件作用: TCP 客户端（运行在 KanziMcpServer 进程中）
// 关键类: KanziPipeClient : IDisposable
// 主要职责:
//   1. 连接到 Kanzi Studio 进程内的 TCP 服务 (localhost:9595)
//   2. 将 MCP 工具调用转发为 JSON 请求，写入 TCP 流
//   3. 读取 TCP 响应，解析为 JsonElement 返回给 ToolHandler
//   4. 超时自动重连机制
//   5. UTF-8 无 BOM 读写（与 TcpServer 对齐）

using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KanziMcpServer.Models;

namespace KanziMcpServer.Services;

/// <summary>
/// TCP 客户端 - 与 Kanzi 插件通信
/// 使用 TCP 代替 Named Pipe，绕过进程安全上下文限制
/// </summary>
public class KanziPipeClient : IDisposable
{
    // TCP 配置 - 与插件端保持一致
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 9595;
    
    private readonly string _host;
    private readonly int _port;
    private readonly int _connectTimeout;
    private readonly int _readTimeout;
    private readonly JsonSerializerOptions _jsonOptions;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private bool _isConnected;
    private DateTime _connectedAt;

    public bool IsConnected => _isConnected && _tcpClient?.Connected == true;
    public DateTime ConnectedAt => _connectedAt;
    public int Port => _port;

    public KanziPipeClient(
        string pipeName = McpConstants.DefaultPipeName, // 忽略，使用 TCP
        int connectTimeout = McpConstants.PipeConnectTimeout,
        int readTimeout = McpConstants.PipeReadTimeout)
    {
        // 从 pipeName 中解析端口（格式: "tcp:9595" 或默认 9595）
        _host = DefaultHost;
        if (pipeName.StartsWith("tcp:"))
        {
            if (int.TryParse(pipeName.Substring(4), out int port))
            {
                _port = port;
            }
            else
            {
                _port = DefaultPort;
            }
        }
        else
        {
            _port = DefaultPort;
        }
        
        _connectTimeout = connectTimeout;
        _readTimeout = readTimeout;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>
    /// 连接到 Kanzi TCP Server (with retries)
    /// </summary>
    public async Task<bool> ConnectAsync()
    {
        if (IsConnected)
            return true;

        int maxRetries = McpConstants.PipeMaxRetries;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // 先清理旧连接
                Disconnect();

                Console.Error.WriteLine($"[KanziPipeClient] Connect attempt {attempt}/{maxRetries} to {_host}:{_port}...");
                
                _tcpClient = new TcpClient();
                
                using var cts = new CancellationTokenSource(_connectTimeout);
                await _tcpClient.ConnectAsync(_host, _port, cts.Token);

                _stream = _tcpClient.GetStream();
                _writer = new StreamWriter(_stream, new UTF8Encoding(false), 1024, leaveOpen: true);
                _reader = new StreamReader(_stream, Encoding.UTF8, false, 1024, leaveOpen: true);

                _isConnected = true;
                _connectedAt = DateTime.UtcNow;

                Console.Error.WriteLine($"[KanziPipeClient] Connected to {_host}:{_port} on attempt {attempt}");
                return _isConnected;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"[KanziPipeClient] Connect attempt {attempt} timed out after {_connectTimeout}ms");
                lastException = new TimeoutException($"Connection timeout after {_connectTimeout}ms");
                if (attempt < maxRetries)
                {
                    await Task.Delay(2000);
                    continue;
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                Console.Error.WriteLine($"[KanziPipeClient] Connect attempt {attempt} failed: {ex.Message}");
                if (attempt < maxRetries)
                {
                    Console.Error.WriteLine($"[KanziPipeClient] Waiting 3s before retry...");
                    await Task.Delay(3000);
                }
            }
        }

        // 所有重试都失败了
        Console.Error.WriteLine($"[KanziPipeClient] All {maxRetries} connection attempts failed");
        if (lastException != null)
        {
            Console.Error.WriteLine($"[KanziPipeClient] Last error: {lastException.Message}");
        }
        _isConnected = false;
        return false;
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public void Disconnect()
    {
        try
        {
            _writer?.Dispose();
            _writer = null;
            _reader?.Dispose();
            _reader = null;
        }
        catch { }

        if (_stream != null)
        {
            try
            {
                _stream.Close();
                _stream.Dispose();
            }
            catch { }
            _stream = null;
        }

        if (_tcpClient != null)
        {
            try
            {
                _tcpClient.Close();
                _tcpClient.Dispose();
            }
            catch { }
            _tcpClient = null;
        }
        _isConnected = false;
    }

    /// <summary>
    /// 发送请求到 Kanzi 并获取响应
    /// </summary>
    private async Task<T> SendRequestAsync<T>(string method, object? args = null, int? readTimeoutMs = null)
    {
        if (!IsConnected)
        {
            var connected = await ConnectAsync();
            if (!connected)
                throw new InvalidOperationException("Cannot connect to Kanzi TCP Server. Ensure Kanzi Studio is running with KanziMCP plugin loaded.");
        }

        var request = new { method, args };
        var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
        var timeoutMs = readTimeoutMs ?? _readTimeout;

        int maxRetries = 2;  // 额外重试次数，总计最多 3 次尝试
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Console.Error.WriteLine($"[KanziPipeClient] Retry attempt {attempt}/{maxRetries}...");
                }

                Console.Error.WriteLine($"[KanziPipeClient] Sending: {requestJson}");
                await _writer!.WriteLineAsync(requestJson);
                await _writer.FlushAsync();
                Console.Error.WriteLine($"[KanziPipeClient] Sent, waiting for response (timeout: {timeoutMs}ms)...");

                using var cts = new CancellationTokenSource(timeoutMs);
                var responseJson = await _reader!.ReadLineAsync(cts.Token);

                Console.Error.WriteLine($"[KanziPipeClient] Received: {(responseJson != null ? responseJson.Substring(0, Math.Min(200, responseJson.Length)) : "null")}");

                if (string.IsNullOrEmpty(responseJson))
                {
                    Console.Error.WriteLine("[KanziPipeClient] Connection closed, will reconnect and retry");
                    Disconnect();
                    var reconnected = await ConnectAsync();
                    if (reconnected)
                    {
                        Console.Error.WriteLine("[KanziPipeClient] Reconnected, retrying request");
                        await _writer!.WriteLineAsync(requestJson);
                        await _writer.FlushAsync();
                        using var retryCts = new CancellationTokenSource(timeoutMs);
                        responseJson = await _reader!.ReadLineAsync(retryCts.Token);
                        if (string.IsNullOrEmpty(responseJson))
                            throw new InvalidOperationException("Connection closed after reconnect");
                    }
                    else
                    {
                        throw new InvalidOperationException("Connection lost, reconnect failed");
                    }
                }

                return ParseResponse<T>(responseJson);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"[KanziPipeClient] Timeout after {timeoutMs}ms (attempt {attempt + 1}/{maxRetries + 1}), disconnecting");
                Disconnect();

                if (attempt < maxRetries)
                {
                    var delayMs = 2000 * (attempt + 1);  // 指数退避: 2s, 4s
                    Console.Error.WriteLine($"[KanziPipeClient] Retrying after {delayMs}ms...");
                    await Task.Delay(delayMs);
                    var reconnected = await ConnectAsync();
                    if (!reconnected)
                        throw new TimeoutException($"Request timeout ({timeoutMs}ms) and reconnect failed. Ensure Kanzi Studio is running.");
                }
                else
                {
                    throw new TimeoutException($"Request timeout after {maxRetries + 1} attempts ({timeoutMs}ms each). Ensure Kanzi Studio has loaded MCP plugin and plugin is running.");
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[KanziPipeClient] I/O error: {ex.Message}, disconnecting");
                Disconnect();
                throw new InvalidOperationException($"TCP I/O error: {ex.Message}");
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>
    /// 解析响应 JSON
    /// </summary>
    private T ParseResponse<T>(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl))
        {
            var errorMessage = errorEl.GetString() ?? "Unknown error";
            throw new InvalidOperationException($"Kanzi Error: {errorMessage}");
        }

        if (root.TryGetProperty("result", out var resultEl))
        {
            if (typeof(T) == typeof(string))
            {
                return (T)(object)resultEl.GetRawText();
            }
            return JsonSerializer.Deserialize<T>(resultEl.GetRawText(), _jsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize response");
        }

        // 兼容旧格式
        if (typeof(T) == typeof(string))
        {
            return (T)(object)responseJson;
        }

        throw new InvalidOperationException($"Invalid response format: missing 'result' or 'error' field");
    }

    #region 节点查询

    public async Task<string> QueryNodesAsync(NodeQueryFilter filter)
    {
        return await SendRequestAsync<string>("query_nodes", filter);
    }

    public async Task<string> GetNodeTreeAsync(string? rootPath, int depth, bool includeProperties = false)
    {
        return await SendRequestAsync<string>("get_node_tree", new { rootPath, depth, includeProperties });
    }

    public async Task<string> ListNodeTypesAsync()
    {
        return await SendRequestAsync<string>("list_node_types");
    }

    public async Task<string> GetBindingInfoAsync(string path)
    {
        return await SendRequestAsync<string>("get_binding_info", new { path });
    }

    public async Task<string> SearchNodesAsync(string searchText, List<string> searchIn, bool caseSensitive)
    {
        return await SendRequestAsync<string>("search_nodes", new { searchText, searchIn, caseSensitive });
    }

    #endregion

    #region 属性操作

    public async Task<string> SetPropertyAsync(string path, string property, JsonElement? value, string mode, bool force)
    {
        return await SendRequestAsync<string>("set_property", new { path, property, value, mode, force });
    }

    public async Task<string> BatchSetPropertyAsync(NodeQueryFilter filter, Dictionary<string, PropertyValue> properties, string mode, bool ignoreReadOnly)
    {
        return await SendRequestAsync<string>("batch_set_property", new { filter, properties, mode, ignoreReadOnly });
    }

    public async Task<string> GetPropertyMetadataAsync(string nodeType)
    {
        return await SendRequestAsync<string>("get_property_metadata", new { nodeType });
    }

    #endregion

    #region 审计工具

    public async Task<string> AuditBindingsAsync(string? path, bool checkPriority, bool findOrphans)
    {
        return await SendRequestAsync<string>("audit_bindings", new { path, checkPriority, findOrphans });
    }

    public async Task<string> AuditLocalizationAsync(List<string> languages)
    {
        return await SendRequestAsync<string>("audit_localization", new { languages });
    }

    public async Task<string> AuditProjectStructureAsync(string? namingPattern, bool checkDepth, bool checkNaming)
    {
        return await SendRequestAsync<string>("audit_project_structure", new { namingPattern, checkDepth, checkNaming });
    }

    /// <summary>
    /// Audit resource references - find unused, broken, or orphaned resources
    /// </summary>
    public async Task<string> AuditResourceReferencesAsync(bool checkUnused = true, bool checkBroken = true, bool checkOrphaned = true)
    {
        return await SendRequestAsync<string>("audit_resource_references", new { checkUnused, checkBroken, checkOrphaned });
    }

    #endregion

    #region 节点创建与删除

    /// <summary>
    /// Create a new node under a parent node
    /// </summary>
    public async Task<string> CreateNodeAsync(string parentPath, string nodeType, string? nodeName, Dictionary<string, object>? properties)
    {
        return await SendRequestAsync<string>("create_node", new { parentPath, nodeType, nodeName, properties });
    }

    /// <summary>
    /// Delete a node (supports preview/dry-run mode)
    /// </summary>
    public async Task<string> DeleteNodeAsync(string path, string mode = "apply")
    {
        return await SendRequestAsync<string>("delete_node", new { path, mode });
    }

    #endregion

    #region 资源导入

    /// <summary>
    /// Import an image into the resource library
    /// </summary>
    public async Task<string> ImportImageAsync(string filePath, string? resourceName = null, string targetFolder = "Textures")
    {
        return await SendRequestAsync<string>("import_image", new { filePath, resourceName, targetFolder });
    }

    /// <summary>
    /// Import a 3D model (FBX) into the resource library
    /// </summary>
    public async Task<string> ImportFbxAsync(string filePath, string? resourceName = null, string targetFolder = "Meshes")
    {
        return await SendRequestAsync<string>("import_fbx", new { filePath, resourceName, targetFolder });
    }

    #endregion

    #region 资源诊断

    /// <summary>
    /// Diagnose resource usage - find unused Image and Texture resources
    /// </summary>
    public async Task<string> DoctorResourceAsync(bool checkImages = true, bool checkTextures = true)
    {
        return await SendRequestAsync<string>("doctor_resource", new { checkImages, checkTextures });
    }

    #endregion

    #region Custom Enum Property

    /// <summary>
    /// Create or update a Custom Enum Property
    /// </summary>
    public async Task<string> UpsertCustomEnumPropertyAsync(string name, List<Dictionary<string, object>> options,
        string? displayName = null, string? category = null, string mode = "preview")
    {
        return await SendRequestAsync<string>("upsert_custom_enum_property",
            new { name, options, displayName, category, mode });
    }

    #endregion

    #region State Manager

    /// <summary>
    /// Create a State Manager with StateGroup, States, and StateObjects.
    /// Supports batching for large state counts.
    /// </summary>
    public async Task<string> CreateStateManagerAsync(
        string managerName, string groupName, string groupProperty,
        List<Dictionary<string, object>> states,
        string bindNodePath = "",
        string mode = "preview",
        bool confirmLargeBatch = false,
        int batchIndex = 0,
        int batchSize = McpConstants.StateManagerRecommendedBatchSize,
        string strategy = "auto",
        int? readTimeoutMs = null,
        int totalStateCount = 0)
    {
        var payloadCount = states.Count;
        var partialPayload = totalStateCount > payloadCount
            || (batchIndex > 0 && batchIndex * batchSize >= payloadCount);
        var statesInBatch = partialPayload
            ? payloadCount
            : Math.Min(batchSize, Math.Max(0, payloadCount - batchIndex * batchSize));
        var timeoutMs = readTimeoutMs
            ?? McpConstants.ComputeStateManagerReadTimeoutMs(statesInBatch, batchIndex);

        return await SendRequestAsync<string>("create_state_manager",
            new { managerName, groupName, groupProperty, states, bindNodePath, mode,
                  confirmLargeBatch, batchIndex, batchSize, strategy, totalStateCount },
            timeoutMs);
    }

    #endregion

    #region 状态

    public async Task<string> GetStatusAsync()
    {
        return await SendRequestAsync<string>("get_status");
    }

    public string GetConnectionStatusString()
    {
        if (_isConnected && _tcpClient?.Connected == true)
        {
            var uptime = DateTime.UtcNow - _connectedAt;
            return $"{{\"connected\": true, \"host\": \"{_host}\", \"port\": {_port}, \"uptime\": \"{uptime:hh\\:mm\\:ss}\"}}";
        }
        else
        {
            return $"{{\"connected\": false, \"host\": \"{_host}\", \"port\": {_port}, \"note\": \"Not connected. Ensure Kanzi Studio is running with KanziMCP plugin loaded.\"}}";
        }
    }

    #endregion

    public void Dispose()
    {
        Disconnect();
    }
}
