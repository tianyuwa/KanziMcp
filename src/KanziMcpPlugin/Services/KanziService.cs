// KanziService.cs — 入口（partial class 主文件）
//
// 文件作用: Kanzi Studio 业务层 — 所有节点/属性/绑定操作的真正执行者
// 关键类: KanziService (partial，按职责拆分至 KanziService.*.cs)
// 拆分文件:
//   KanziService.Reflection.cs   — 反射辅助
//   KanziService.Nodes.Query.cs  — 节点查询
//   KanziService.Nodes.Mutate.cs — 节点创建/删除
//   KanziService.Properties.cs   — 属性读写
//   KanziService.Audit.cs        — 审计工具
//   KanziService.Status.cs       — 状态
//   KanziService.Resources.cs    — 资源导入/诊断
//   KanziService.Helpers.cs           — 序列化/日志/过滤器
//   KanziService.CustomProperties.cs  — 自定义枚举属性
//   KanziService.StateManager.cs      — 状态机创建
//   Models/NodeFilter.cs, Models/PropertyMetadata.cs
// 主要职责:
//   1. 通过反射调用 Kanzi Studio Plugin API（避免硬依赖具体版本）
//   2. 节点查询: QueryNodes / GetNodeTree / SearchNodes
//   3. 属性读写: GetItemProperties / TryReadPropertyValue / SetProperty
//   4. 数据绑定: GetBindingInfo
//   5. 审计工具: AuditBindings / AuditLocalization / AuditProjectStructure
//   6. 安全序列化: SafeSerialize / MakeSafeForSerialization（处理不可序列化类型）
// 核心反射策略:
//   - GetActiveProject(): 5 路查找（FlattenHierarchy → 继承链 → 接口 → Project 属性 → 扫描）
//   - GetProjectItem(): 路径拆分 + Children 遍历（因无 GetProjectItem(string) 方法）
//   - GetChildren(): 只用 Children 属性（避免扫描到 CustomIcon/Icon 等非节点属性）
//   - TryReadPropertyValue(): 5 策略读取 DynamicProperty.Value
// 依赖: Rightware.Kanzi.Studio.PluginInterface（Kanzi 安装目录 CLR 加载）
// 日志: 所有操作写入 C:\temp\KanziMcpPlugin.log

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rightware.Kanzi.Studio.PluginInterface;

namespace KanziMcpPlugin.Services
{
    /// <summary>
    /// Kanzi 服务 - 与 Kanzi Studio Plugin API 交互
    ///
    /// 通过 KanziStudio 对象访问项目节点、属性等信息。
    /// 使用反射调用 API，避免硬依赖 Kanzi 内部类型。
    ///
    /// 基于 KanziApiDump (3.9.10) 的真实 API 路径：
    /// - KanziStudio.ActiveProject → PluginInterface.Project
    /// - ProjectItemInterface.Children → IEnumerable<ProjectItem>
    /// - ProjectItem.Name, ProjectItem.Path
    /// - Project.GetChildByName(string)
    /// - ProjectItemInterface.NodeComponentTypeLibrary
    /// </summary>
    public partial class KanziService
    {
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false  // 必须是 false！Pipe 用 ReadLine/WriteLine 通信，换行符会截断消息
        };

        private KanziStudio? _studio;
        private bool _isProjectOpen;
        private string _projectName = "";
        private bool HasStudio => _studio != null;

        public KanziService() { }

        /// <summary>
        /// 注入 KanziStudio 实例
        /// </summary>
        public void SetKanziStudio(KanziStudio studio)
        {
            _studio = studio;
            Log("KanziStudio instance injected");

            // 订阅项目事件
            studio.ProjectOpened += (s, e) =>
            {
                _isProjectOpen = true;
                _projectName = e.Project?.Name ?? "";
                Log($"Project opened: {_projectName}");
            };
            studio.ProjectClosed += (s, e) =>
            {
                _isProjectOpen = false;
                _projectName = "";
                Log("Project closed");
            };

            // 检查是否已有项目打开
            try
            {
                var project = GetActiveProject();
                if (project != null)
                {
                    _isProjectOpen = true;
                    var name = SafeGetProperty(project, "Name") as string;
                    _projectName = name ?? "";
                    Log($"Project already open: {_projectName}");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to check initial project state: {ex.Message}");
            }
        }
    }
}
