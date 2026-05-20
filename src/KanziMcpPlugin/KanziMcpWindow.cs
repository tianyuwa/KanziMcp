// KanziMcpWindow.cs
//
// 文件作用: Kanzi Studio 插件面板窗口
// 关键类: KanziMcpWindow : UserControl, PluginWindow
// 主要职责:
//   1. 实现 PluginWindow 接口，在 Kanzi Studio 侧边栏显示插件面板
//   2. 显示管道连接状态（_statusText）
//   3. 可扩展：在此窗口中添加按钮/日志显示等 UI 元素
// 依赖: Rightware.Kanzi.Studio.PluginInterface（Kanzi 安装目录 CLR 加载）

using System;
using System.Windows.Controls;
using Rightware.Kanzi.Studio.PluginInterface;

namespace KanziMcpPlugin
{
    /// <summary>
    /// Kanzi MCP Plugin 的窗口控件
    /// 实现 PluginWindow 接口，在 Kanzi Studio 中显示插件面板
    /// </summary>
    public class KanziMcpWindow : UserControl, PluginWindow, IDisposable
    {
        private readonly KanziStudio? _studio;
        private readonly TextBlock _statusText;

        public KanziMcpWindow(KanziStudio? studio)
        {
            _studio = studio;

            // 创建简单的状态显示 UI
            var stackPanel = new StackPanel { Margin = new System.Windows.Thickness(10) };

            var titleText = new TextBlock
            {
                Text = "Kanzi MCP Plugin",
                FontSize = 16,
                FontWeight = System.Windows.FontWeights.Bold,
                Margin = new System.Windows.Thickness(0, 0, 0, 10)
            };

            _statusText = new TextBlock
            {
                Text = "MCP Server is running...\nNamed Pipe: KanziMcpPipe\n\nWaiting for AI assistant connections.",
                TextWrapping = System.Windows.TextWrapping.Wrap
            };

            stackPanel.Children.Add(titleText);
            stackPanel.Children.Add(_statusText);

            this.Content = stackPanel;
        }

        #region PluginWindow 接口实现

        /// <summary>窗口标题</summary>
        public string Title => "Kanzi MCP";

        /// <summary>图标（空字符串使用默认图标）</summary>
        public string Icon => "";

        /// <summary>标题变更事件</summary>
        public event EventHandler? TitleChanged;

        /// <summary>序列化窗口状态</summary>
        public PluginWindowState SerializeState()
        {
            return null;
        }

        #endregion

        #region IDisposable 接口实现

        public void Dispose()
        {
            // 清理资源
        }

        #endregion
    }
}
