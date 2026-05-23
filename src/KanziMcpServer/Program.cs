// Program.cs
//
// 文件作用: MCP Server 入口点（.NET 10 独立进程）
// 关键类: Program（静态入口）
// 主要职责:
//   1. 从 stdin 读取 JSON-RPC 请求（无限循环）
//   2. 将请求交给 McpProtocolHandler 处理
//   3. 将响应写回 stdout（JSON 序列化）
//   4. 处理 --verbose 参数（详细日志输出到 stderr）
//   5. 初始化所有服务（ToolHandler + KanziPipeClient + McpProtocolHandler）
// 运行方式:
//   - 作为 MCP Server 被 AI 客户端（如 Cursor/Claude Desktop）启动
//   - 也可独立运行：KanziMcpServer.exe --verbose
// 依赖: McpProtocolHandler, ToolHandler, KanziPipeClient

using System.Diagnostics;
using System.Text.Json;
using KanziMcpServer.Handlers;
using KanziMcpServer.Models;
using KanziMcpServer.Services;

namespace KanziMcpServer;

/// <summary>
/// Kanzi MCP Server - JSON-RPC over stdio
/// 
/// This server implements the Model Context Protocol (MCP) to allow
/// AI assistants (like Claude Code) to interact with Kanzi Studio projects.
/// </summary>
class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    static async Task<int> Main(string[] args)
    {
        Console.Error.WriteLine($"[KanziMcpServer] Starting v{McpConstants.ServerVersion}...");

        // 解析命令行参数
        var options = ParseArguments(args);

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.Verbose)
        {
            Console.Error.WriteLine("[KanziMcpServer] Verbose mode enabled");
        }

        // 创建组件
        var pipeClient = new KanziPipeClient(
            options.PipeName,
            options.ConnectTimeout,
            options.ReadTimeout);

        var toolHandler = new ToolHandler(pipeClient);
        var protocolHandler = new McpProtocolHandler(toolHandler, pipeClient);

        // 尝试连接 Kanzi（后台异步，不阻塞 stdio 主循环）
        if (options.AutoConnect)
        {
            Console.Error.WriteLine($"[KanziMcpServer] Background connecting to {options.PipeName}...");
            _ = Task.Run(async () =>
            {
                var connected = await pipeClient.ConnectAsync();
                if (connected)
                    Console.Error.WriteLine("[KanziMcpServer] Background: connected to Kanzi");
                else
                    Console.Error.WriteLine("[KanziMcpServer] Background: could not connect to Kanzi. Will retry on first request.");
            });
        }

        // 主循环：读取 stdin，处理请求，写入 stdout
        Console.Error.WriteLine("[KanziMcpServer] Ready. Waiting for requests...");
        Console.Error.WriteLine("[KanziMcpServer] Press Ctrl+C to exit");

        try
        {
            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (options.Verbose)
                {
                    Console.Error.WriteLine($"[KanziMcpServer] Received: {line.Substring(0, Math.Min(100, line.Length))}...");
                }

                try
                {
                    var sw = Stopwatch.StartNew();
                    var response = await protocolHandler.HandleRequestAsync(line);
                    sw.Stop();
                    Console.WriteLine(response);

                    var requestPreview = line.Length <= 80 ? line : line.Substring(0, 80);
                    Console.Error.WriteLine($"[KanziMcpServer] [{sw.ElapsedMilliseconds}ms] {requestPreview}");

                    if (options.Verbose)
                    {
                        Console.Error.WriteLine($"[KanziMcpServer] Sent response");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[KanziMcpServer] Error: {ex.Message}");
                    var errorResponse = new
                    {
                        jsonrpc = "2.0",
                        id = 0,
                        error = new
                        {
                            code = McpConstants.ErrorInternalError,
                            message = ex.Message
                        }
                    };
                    Console.WriteLine(JsonSerializer.Serialize(errorResponse, JsonOptions));
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[KanziMcpServer] Shutdown requested");
        }
        finally
        {
            pipeClient.Dispose();
            Console.Error.WriteLine("[KanziMcpServer] Exiting");
        }

        return 0;
    }

    /// <summary>
    /// 解析命令行参数
    /// </summary>
    private static CommandLineOptions ParseArguments(string[] args)
    {
        var options = new CommandLineOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLower();

            switch (arg)
            {
                case "--help" or "-h":
                    options.ShowHelp = true;
                    break;

                case "--pipe" or "-p":
                    if (i + 1 < args.Length)
                        options.PipeName = args[++i];
                    break;

                case "--timeout" or "-t":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var timeout))
                        options.ConnectTimeout = timeout;
                    break;

                case "--verbose" or "-v":
                    options.Verbose = true;
                    break;

                case "--no-auto-connect":
                    options.AutoConnect = false;
                    break;

                case "--version":
                    Console.WriteLine($"KanziMcpServer v{McpConstants.ServerVersion}");
                    Environment.Exit(0);
                    break;
            }
        }

        return options;
    }

    /// <summary>
    /// 打印帮助信息
    /// </summary>
    private static void PrintHelp()
    {
        Console.WriteLine($"""
            Kanzi MCP Server v{McpConstants.ServerVersion}
            
            Usage: KanziMcpServer [options]

            Options:
              --help, -h          Show this help message
              --version           Show version information
              --pipe, -p <name>   Named pipe name (default: {McpConstants.DefaultPipeName})
              --timeout, -t <ms>  Connection timeout in milliseconds (default: {McpConstants.PipeConnectTimeout})
              --verbose, -v       Enable verbose output
              --no-auto-connect   Don't connect automatically on startup

            Environment Variables:
              KANZI_PIPE_NAME     Named pipe name
              KANZI_CONNECT_TIMEOUT Connection timeout (ms)

            Examples:
              KanziMcpServer
              KanziMcpServer --pipe KanziMcpPipe --verbose
              KanziMcpServer -p MyPipe -t 10000

            Protocol:
              This server implements the Model Context Protocol (MCP) over stdio.
              It communicates with a Kanzi Studio plugin via Named Pipes.
            """);
    }
}

/// <summary>
/// 命令行选项
/// </summary>
internal class CommandLineOptions
{
    public bool ShowHelp { get; set; }
    public string PipeName { get; set; } = Environment.GetEnvironmentVariable("KANZI_PIPE_NAME") ?? McpConstants.DefaultPipeName;
    public int ConnectTimeout { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("KANZI_CONNECT_TIMEOUT"), out var ct) ? ct : McpConstants.PipeConnectTimeout;
    public int ReadTimeout { get; set; } = McpConstants.PipeReadTimeout;
    public bool Verbose { get; set; }
    public bool AutoConnect { get; set; } = true;
}
