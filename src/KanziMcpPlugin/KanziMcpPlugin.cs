// KanziMcpPlugin.cs
//
// 文件作用: Kanzi Studio 插件入口点（MEF 插件入口）
// 关键类: KanziMcpPlugin : PluginWindowFactory
// 主要职责:
//   1. 实现 PluginWindowFactory 接口，通过 [Export] 被 Kanzi Studio MEF 加载
//   2. Initialize() 中启动 KanziPipeServer（Named Pipe 服务端）
//   3. 注入 KanziStudio 实例到 KanziService，建立 MCP 通道
//   4. 处理插件窗口创建（KanziMcpWindow）
// 依赖: Rightware.Kanzi.Studio.PluginInterface（通过 Kanzi 安装目录 CLR 加载）
// 部署位置: C:\ProgramData\Rightware\Kanzi 3.9.10\plugins\PluginKanziMCP.dll

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using Rightware.Kanzi.Studio.PluginInterface;
using KanziMcpPlugin.PipeServer;

namespace KanziMcpPlugin
{
    /// <summary>
    /// Kanzi MCP Plugin - Kanzi Studio 插件入口
    ///
    /// 实现 PluginWindowFactory 接口，Kanzi Studio 通过 MEF [Export] 属性发现此类并加载。
    /// 插件启动 Named Pipe Server 以接收来自 MCP Server 的请求。
    /// </summary>
    [Export(typeof(PluginContent))]
    public class KanziMcpPlugin : PluginWindowFactory
    {
        private KanziPipeServer? _pipeServer;
        private KanziStudio? _studio;

        /// <summary>
        /// 静态构造函数 - 注册 AssemblyResolve 事件
        /// 确保 plugins 目录下的依赖 DLL 能被正确加载
        /// </summary>
        static KanziMcpPlugin()
        {
            // 写文件日志，确认静态构造函数被调用（MEF 发现此类）
            // 用 try-catch 包裹，防止目录不存在导致类型加载失败
            try
            {
                Directory.CreateDirectory(@"C:\temp");
                File.AppendAllText(
                    @"C:\temp\KanziMcpPlugin.log",
                    $"[{DateTime.Now}] Static constructor called{Environment.NewLine}");
            }
            catch
            {
                // 忽略日志写入失败
            }

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        /// <summary>
        /// 当默认程序集解析失败时，从 plugins 目录查找依赖 DLL
        /// </summary>
        private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
        {
            try
            {
                var log = (string msg) => File.AppendAllText(
                    @"C:\temp\KanziMcpPlugin.log",
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");

                // 获取插件目录 — MEF 加载时 Location 可能为空，需多路回退
                var location = Assembly.GetExecutingAssembly().Location;
                string? pluginDir = null;

                if (!string.IsNullOrEmpty(location))
                {
                    pluginDir = Path.GetDirectoryName(location);
                }

                // 回退1: 用 CodeBase (file:/// URI 格式)
                if (string.IsNullOrEmpty(pluginDir))
                {
                    try
                    {
                        var codeBase = Assembly.GetExecutingAssembly().CodeBase;
                        if (!string.IsNullOrEmpty(codeBase))
                        {
                            var uri = new Uri(codeBase);
                            pluginDir = Path.GetDirectoryName(uri.LocalPath);
                        }
                    }
                    catch { }
                }

                // 回退2: 尝试 Kanzi plugins 目录
                if (string.IsNullOrEmpty(pluginDir))
                {
                    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    var kanziPluginDir = Path.Combine(programData, "Rightware", "Kanzi 3.9.10", "plugins");
                    if (Directory.Exists(kanziPluginDir))
                    {
                        pluginDir = kanziPluginDir;
                    }
                }

                if (string.IsNullOrEmpty(pluginDir))
                {
                    log("AssemblyResolve: pluginDir is empty (Location, CodeBase, CommonAppData all failed)");
                    return null;
                }

                var assemblyName = new AssemblyName(args.Name).Name;
                var dllPath = Path.Combine(pluginDir, assemblyName + ".dll");

                log($"AssemblyResolve: trying {dllPath} (from {pluginDir})");

                if (File.Exists(dllPath))
                {
                    log($"AssemblyResolve: loading {assemblyName} from {dllPath}");
                    return Assembly.LoadFrom(dllPath);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        @"C:\temp\KanziMcpPlugin.log",
                        $"[{DateTime.Now:HH:mm:ss.fff}] AssemblyResolve error: [{ex.GetType().Name}] {ex.Message}{Environment.NewLine}");
                }
                catch { }
            }
            return null;
        }

        #region PluginContent 接口实现

        /// <summary>插件唯一标识名</summary>
        public string Name => "KanziMCP";

        /// <summary>插件显示名称（在 Kanzi Studio 菜单中显示）</summary>
        public string DisplayName => "Kanzi MCP Plugin";

        /// <summary>插件描述</summary>
        public string Description => "Enables AI assistants (Claude, etc.) to interact with Kanzi Studio via MCP protocol. Provides tools for node querying, property editing, binding auditing and more.";

        /// <summary>
        /// Kanzi Studio 调用此方法初始化插件
        /// </summary>
        public void Initialize(KanziStudio studio)
        {
            _studio = studio;

            var log = (string msg) =>
            {
                try
                {
                    Directory.CreateDirectory(@"C:\temp");
                    File.AppendAllText(
                        @"C:\temp\KanziMcpPlugin.log",
                        $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
                }
                catch { }
            };

            log("Initialize() called");
            log($"Kanzi Studio version: {studio.Version}");

            // 诊断: 输出多种方式获取的插件路径
            var location = Assembly.GetExecutingAssembly().Location;
            var codeBase = Assembly.GetExecutingAssembly().CodeBase;
            log($"Assembly.Location: '{location}'");
            log($"Assembly.CodeBase: '{codeBase}'");

            // 订阅项目事件
            studio.ProjectOpened += OnProjectOpened;
            studio.ProjectClosed += OnProjectClosed;

            // 创建并启动 Named Pipe Server
            try
            {
                log("Creating KanziPipeServer...");
                _pipeServer = new KanziPipeServer();
                _pipeServer.SetKanziStudio(studio);

                // 运行时反射：将 Kanzi Studio 真实 API 面写到日志
                try
                {
                    log("Dumping Kanzi Studio API...");
                    Services.KanziApiDumper.DumpApi(studio);
                    log("API dump completed");
                }
                catch (Exception dumpEx)
                {
                    log($"API dump failed: [{dumpEx.GetType().Name}] {dumpEx.Message}");
                }

                log("Starting PipeServer...");
                _pipeServer.Start();
                log("PipeServer.Start() returned successfully");
            }
            catch (Exception ex)
            {
                log($"PipeServer START FAILED: [{ex.GetType().Name}] {ex.Message}");
                log($"Stack trace: {ex.StackTrace}");
                // 不重新抛出 — 插件仍然可以加载（只是无法响应 MCP 请求）
            }
        }

        #endregion

        #region PluginWindowFactory 接口实现

        /// <summary>创建插件窗口</summary>
        public PluginWindow CreateWindow(PluginWindowState state)
        {
            // 创建简单的 MCP 控制窗口
            return new KanziMcpWindow(_studio);
        }

        /// <summary>默认窗口宽度</summary>
        public uint DefaultWidth => 400;

        /// <summary>默认窗口高度</summary>
        public uint DefaultHeight => 300;

        #endregion

        #region PluginCommandBase 接口实现

        /// <summary>菜单放置位置 - 在主菜单中显示</summary>
        public CommandPlacement CommandPlacement =>
            new CommandPlacement("KanziMCP", ContextMenuPlacement.NONE, false, null);

        /// <summary>是否可以执行插件命令</summary>
        public bool CanExecute(PluginCommandParameter parameter)
        {
            // 只要有打开的项目就可以执行
            return _studio != null && _studio.ActiveProject != null;
        }

        #endregion

        private void OnProjectOpened(object? sender, ProjectEventArgs e)
        {
            Console.WriteLine($"[KanziMcpPlugin] Project opened: {e.Project?.Name}");
        }

        private void OnProjectClosed(object? sender, ProjectPathEventArgs e)
        {
            Console.WriteLine($"[KanziMcpPlugin] Project closed: {e.ProjectPath}");
        }
    }
}
