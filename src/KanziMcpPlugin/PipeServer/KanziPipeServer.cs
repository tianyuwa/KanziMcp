// KanziTcpServer.cs
//
// 文件作用: TCP 服务端（运行在 Kanzi Studio 进程内）
// 关键类: KanziTcpServer : IDisposable
// 主要职责:
//   1. 使用 TcpListener 在 localhost:9595 监听连接
//   2. 监听来自 KanziMcpServer（独立进程）的 JSON-RPC 请求
//   3. 将请求路由到 KanziService 处理，并将结果写回客户端
//   4. 处理 UTF-8 无 BOM 的流读写（避免 JSON 解析错误）
// 依赖: KanziService（同一程序集内）

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KanziMcpPlugin.Services;
using Rightware.Kanzi.Studio.PluginInterface;

namespace KanziMcpPlugin.PipeServer
{
    /// <summary>
    /// TCP Server - 接收 MCP Server 请求并转发给 Kanzi
    /// 使用 TCP 代替 Named Pipe，绕过进程安全上下文限制
    /// </summary>
    public class KanziTcpServer : IDisposable
    {
        // TCP 监听端口 - 使用 localhost 避免网络暴露
        private const int DefaultPort = 9595;
        private readonly int _port;
        private readonly KanziService _kanziService;
        private CancellationTokenSource? _cts;
        private Task? _listenerTask;
        private TcpListener? _listener;
        private bool _isRunning;
        private int _connectionCount;
        private System.Threading.SynchronizationContext? _syncContext;

        // TCP read/send timeout — align with KanziMcpServer batch timeout (600s)
        private const int TcpOperationTimeoutMs = 600000;

        // 日志级别控制: 0=关键, 1=信息, 2=详细
        private const int LogLevel = 1;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public bool IsRunning => _isRunning;
        public int ConnectionCount => _connectionCount;
        public int Port => _port;

        private const string LogFilePath = @"C:\temp\KanziMcpPlugin.log";
        private const long MaxLogFileSize = 1 * 1024 * 1024; // 1MB

        private void Log(string msg, int level = 1)
        {
            if (level > LogLevel) return;
            try
            {
                Directory.CreateDirectory(@"C:\temp");
                
                // 日志轮转
                var logFile = new FileInfo(LogFilePath);
                if (logFile.Exists && logFile.Length > MaxLogFileSize)
                {
                    File.WriteAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [TcpServer] Log truncated{Environment.NewLine}");
                }
                
                File.AppendAllText(LogFilePath, $"[{DateTime.Now:HH:mm:ss.fff}] [TcpServer] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        public KanziTcpServer(int port = DefaultPort)
        {
            _port = port;
            _kanziService = new KanziService();
            Log($"KanziTcpServer constructed, port={_port}");
        }

        /// <summary>
        /// 注入 KanziStudio 实例
        /// </summary>
        public void SetKanziStudio(KanziStudio studio)
        {
            _kanziService.SetKanziStudio(studio);
        }

        /// <summary>
        /// 启动 TCP Server
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                Log("Already running");
                return;
            }

            Log("Starting TcpServer...");

            // 捕获 UI 线程同步上下文（Kanzi Studio 在 UI 线程调用 Start）
            _syncContext = System.Threading.SynchronizationContext.Current;
            Log(_syncContext != null
                ? $"SynchronizationContext captured: {_syncContext.GetType().Name}"
                : "WARNING: No SynchronizationContext available, requests will run on thread pool threads");

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
                Log($"TcpListener started on localhost:{_port}");

                _cts = new CancellationTokenSource();
                _listenerTask = ListenAsync(_cts.Token);
                _isRunning = true;
                _connectionCount = 0;

                Log($"Started on port: {_port}");
            }
            catch (Exception ex)
            {
                Log($"Failed to start TcpListener: [{ex.GetType().Name}] {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 停止 TCP Server
        /// </summary>
        public void Stop()
        {
            if (!_isRunning)
                return;

            Log("Stopping...");
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
                _listener = null;
                _listenerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            _isRunning = false;
            Log("Stopped");
        }

        /// <summary>
        /// 异步监听连接
        /// </summary>
        private async Task ListenAsync(CancellationToken ct)
        {
            Log($"ListenAsync started, port={_port}");

            while (!ct.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    Log("Waiting for client connection...", 1);
                    
                    // .NET Framework: 使用 Task.Run 包装同步 AcceptTcpClient
                    client = await Task.Run(() => _listener!.AcceptTcpClient(), ct);
                    
                    _connectionCount++;
                    Log($"Client connected from {client.Client.RemoteEndPoint} (total: {_connectionCount})");
                    _ = HandleClientAsync(client, ct);
                }
                catch (OperationCanceledException)
                {
                    Log("Cancelled, exiting listen loop");
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Listen error: [{ex.GetType().Name}] {ex.Message}");
                    try { client?.Close(); } catch { }
                    
                    // 短暂等待后重试
                    try { Task.Delay(500, ct).Wait(ct); } catch { break; }
                }
            }
            Log("ListenAsync exited");
        }

        /// <summary>
        /// 处理客户端连接
        /// </summary>
        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
                {
                    client.ReceiveTimeout = TcpOperationTimeoutMs;
                    client.SendTimeout = TcpOperationTimeoutMs;

                    while (client.Connected && !ct.IsCancellationRequested)
                    {
                        // .NET Framework: ReadLineAsync 不支持 CancellationToken
                        // 使用 Task.Run + Timeout 模拟
                        var readTask = Task.Run(() => reader.ReadLine(), ct);
                        var timeoutTask = Task.Delay(TcpOperationTimeoutMs, ct);
                        
                        var completedTask = await Task.WhenAny(readTask, timeoutTask);
                        if (completedTask == timeoutTask)
                        {
                            Log("Read timeout, closing connection", 1);
                            break;
                        }
                        
                        string? request = readTask.Result;
                        if (string.IsNullOrEmpty(request))
                        {
                            Log("Client disconnected", 1);
                            break;
                        }

                        Log($"Request: {request.Substring(0, Math.Min(120, request.Length))}", 2);

                        // 将请求处理调度到 UI 线程（Kanzi Studio API 需要 UI 线程访问）
                        string response;
                        if (_syncContext != null)
                        {
                            var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
                            _syncContext.Post(_ =>
                            {
                                try
                                {
                                    tcs.SetResult(ProcessRequest(request));
                                }
                                catch (Exception ex)
                                {
                                    tcs.SetException(ex);
                                }
                            }, null);
                            response = await tcs.Task;
                        }
                        else
                        {
                            response = ProcessRequest(request);
                        }

                        Log($"Response: {response.Substring(0, Math.Min(120, response.Length))}", 2);
                        
                        await writer.WriteLineAsync(response);
                        await writer.FlushAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"HandleClient error: [{ex.GetType().Name}] {ex.Message}");
            }
            finally
            {
                Log("Client disconnected, connection closed");
            }
        }

        /// <summary>
        /// 处理请求
        /// </summary>
        private string ProcessRequest(string requestJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(requestJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("method", out var methodEl))
                {
                    return CreateErrorResponse("Missing method field");
                }

                var method = methodEl.GetString() ?? "";
                JsonElement? args = null;
                if (root.TryGetProperty("args", out var argsEl))
                    args = argsEl;

                var resultJson = method switch
                {
                    // Node queries
                    "query_nodes" => _kanziService.QueryNodes(args),
                    "get_node_tree" => _kanziService.GetNodeTree(args),
                    "list_node_types" => _kanziService.ListNodeTypes(args),
                    "get_binding_info" => _kanziService.GetBindingInfo(args),
                    "search_nodes" => _kanziService.SearchNodes(args),

                    // Property operations
                    "set_property" => _kanziService.SetProperty(args),
                    "batch_set_property" => _kanziService.BatchSetProperty(args),
                    "get_property_metadata" => _kanziService.GetPropertyMetadata(args),

                    // Audit
                    "audit_bindings" => _kanziService.AuditBindings(args),
                    "audit_localization" => _kanziService.AuditLocalization(args),
                    "audit_project_structure" => _kanziService.AuditProjectStructure(args),
                    "audit_resource_references" => _kanziService.AuditResourceReferences(args),

                    // Node creation and deletion
                    "create_node" => _kanziService.CreateNode(args),
                    "delete_node" => _kanziService.DeleteNode(args),

                    // Resource import
                    "import_image" => _kanziService.ImportImage(args),
                    "import_fbx" => _kanziService.ImportFbx(args),

                    // Resource diagnosis
                    "doctor_resource" => _kanziService.DoctorResource(args),

                    // Custom Enum Property
                    "upsert_custom_enum_property" => _kanziService.UpsertCustomEnumProperty(args),

                    // State Manager
                    "create_state_manager" => _kanziService.CreateStateManager(args),

                    // Status
                    "get_status" => _kanziService.GetStatus(args),

                    _ => CreateErrorResponse($"Unsupported method: {method}")
                };

                // Wrap in protocol format
                try
                {
                    using var resultDoc = JsonDocument.Parse(resultJson);
                    if (resultDoc.RootElement.TryGetProperty("error", out _))
                    {
                        return resultJson;
                    }
                    return $"{{\"result\":{resultJson}}}";
                }
                catch
                {
                    return $"{{\"result\":{JsonSerializer.Serialize(resultJson, _jsonOptions)}}}";
                }
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Request processing error: {ex.Message}");
            }
        }

        private string CreateErrorResponse(string message)
        {
            return JsonSerializer.Serialize(new { error = message }, _jsonOptions);
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }

    /// <summary>
    /// 兼容性别名 - 保持原有命名以便其他代码引用
    /// </summary>
    public class KanziPipeServer : IDisposable
    {
        private readonly KanziTcpServer _tcpServer;

        public bool IsRunning => _tcpServer.IsRunning;
        public int ConnectionCount => _tcpServer.ConnectionCount;

        public KanziPipeServer(string? pipeName = null)
        {
            // 忽略 pipeName 参数，使用 TCP
            _tcpServer = new KanziTcpServer();
            Log($"KanziPipeServer (TCP mode) constructed");
        }

        public void SetKanziStudio(KanziStudio studio)
        {
            _tcpServer.SetKanziStudio(studio);
        }

        public void Start()
        {
            _tcpServer.Start();
        }

        public void Stop()
        {
            _tcpServer.Stop();
        }

        private void Log(string msg, int level = 1)
        {
            try
            {
                Directory.CreateDirectory(@"C:\temp");
                File.AppendAllText(@"C:\temp\KanziMcpPlugin.log", $"[{DateTime.Now:HH:mm:ss.fff}] [PipeServer] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        public void Dispose()
        {
            _tcpServer.Dispose();
        }
    }
}
