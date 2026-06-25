// 调用边界规则:
// - 本文件包含无 SDK 公开 API 的反射方法，属于永久黑盒区。
// - 禁止尝试将本文件中的任何方法替换为 SDK 强类型。
// - 若 Kanzi SDK 未来提供等价 API，需先与架构组评审后再迁移。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KanziMcpPlugin.Services
{
    public partial class KanziService
    {
        // ============================================================
        // 永久黑盒区 — 无 SDK 公开 API
        // 以下方法必须保留反射实现，禁止尝试 SDK 化。
        // 若需修改，必须先与架构组评审。
        // ============================================================

        /// <summary>
        /// 通过反射执行 Kanzi Plugin Command（KanziUIEnvironment）。
        /// Presentation 层 API，不在 PluginInterface 中，必须保留反射。
        /// </summary>
        private bool TryExecuteKanziPluginCommand(string commandName, object parentItem, string nodeType,
            string? nodeName, out object? newNode)
        {
            newNode = null;
            try
            {
                var envType = FindTypeInAssemblies("KanziUIEnvironment")
                    ?? FindTypeInAssemblies("Rightware.Kanzi.Tool.Presentation.Application.KanziUIEnvironment");
                if (envType == null)
                    return false;

                var getPluginCommand = envType.GetMethod("GetPluginCommand",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (getPluginCommand == null)
                    return false;

                object? pluginCommand = null;
                try
                {
                    pluginCommand = getPluginCommand.Invoke(null, new object[] { commandName });
                }
                catch { }

                if (pluginCommand == null)
                {
                    var getAll = envType.GetMethod("GetPluginCommands", BindingFlags.Public | BindingFlags.Static);
                    var searchToken = commandName.StartsWith("Create", StringComparison.OrdinalIgnoreCase)
                        ? commandName.Substring(6)
                        : commandName;
                    if (getAll?.Invoke(null, null) is IEnumerable commands)
                    {
                        foreach (var cmd in commands)
                        {
                            var cmdName = cmd?.GetType().GetProperty("Name")?.GetValue(cmd)?.ToString()
                                ?? cmd?.GetType().GetProperty("CommandName")?.GetValue(cmd)?.ToString()
                                ?? cmd?.ToString();
                            if (cmdName != null &&
                                (cmdName.Contains(searchToken, StringComparison.OrdinalIgnoreCase) ||
                                 cmdName.Contains(commandName, StringComparison.OrdinalIgnoreCase)))
                            {
                                pluginCommand = cmd;
                                Log($"TryExecuteKanziPluginCommand: resolved '{commandName}' -> '{cmdName}'");
                                break;
                            }
                        }
                    }
                }

                if (pluginCommand == null || _studio == null)
                    return false;

                var listType = typeof(List<>).MakeGenericType(parentItem.GetType());
                var itemsList = (System.Collections.IList)Activator.CreateInstance(listType)!;
                itemsList.Add(parentItem);

                MethodInfo? execByCommand = null;
                MethodInfo? execByName = null;
                foreach (var m in _studio.GetType().GetMethods(
                             BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                {
                    if (m.Name != "ExecutePluginCommand") continue;
                    var p = m.GetParameters();
                    if (p.Length != 2) continue;
                    if (p[0].ParameterType == typeof(string))
                        execByName = m;
                    else if (p[0].ParameterType.IsInstanceOfType(pluginCommand) ||
                             p[0].ParameterType.IsAssignableFrom(pluginCommand.GetType()))
                        execByCommand = m;
                }

                if (execByCommand != null)
                    execByCommand.Invoke(_studio, new[] { pluginCommand, itemsList });
                else if (execByName != null)
                    execByName.Invoke(_studio, new object[] { commandName, itemsList });
                else
                    return false;

                var childName = nodeName ?? $"New{nodeType}";
                newNode = GetChildren(parentItem).FirstOrDefault(c =>
                {
                    var n = GetItemName(c);
                    return n == childName || n.Contains(nodeType, StringComparison.OrdinalIgnoreCase);
                });
                return newNode != null;
            }
            catch (Exception ex)
            {
                Log($"TryExecuteKanziPluginCommand({commandName}) failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 将 PluginInterface.ProjectItem 转换为内部类型。
        /// PluginWrapper 有多个同名 WrappedItem（泛型接口），GetProperty 会歧义，
        /// 必须用 GetProperties 逐个找。回退到全程序集扫描 GetProjectItemFor 静态方法。
        /// </summary>
        private object? GetInternalProjectItem(object? pluginItem)
        {
            if (pluginItem == null) return null;
            try
            {
                var typeName = pluginItem.GetType().Name;
                if (SafeGetProperty(pluginItem, "HasPluginWrapper") as bool? == true)
                    return pluginItem;

                // 策略1: GetProperties 逐个找 WrappedItem（避开同名歧义）
                foreach (var prop in pluginItem.GetType().GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                {
                    if (prop.Name != "WrappedItem") continue;
                    try
                    {
                        var val = prop.GetValue(pluginItem);
                        if (val != null && val != pluginItem && val.GetType() != pluginItem.GetType())
                        {
                            Log($"GetInternalProjectItem: WrappedItem {typeName} -> {val.GetType().Name}");
                            return val;
                        }
                    }
                    catch { }
                }

                // 策略2: 全程序集扫描 GetProjectItemFor / GetPluginWrapperFor 静态方法
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in assembly.GetTypes())
                        {
                            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            {
                                if ((m.Name != "GetProjectItemFor" && m.Name != "GetPluginWrapperFor") ||
                                    m.GetParameters().Length != 1) continue;
                                try
                                {
                                    var result = m.Invoke(null, new object[] { pluginItem });
                                    if (result != null && result != pluginItem && result.GetType() != pluginItem.GetType())
                                    {
                                        Log($"GetInternalProjectItem: {m.DeclaringType!.Name}.{m.Name} {typeName} -> {result.GetType().Name}");
                                        return result;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log($"GetInternalProjectItem error: {ex.Message}"); }
            Log($"GetInternalProjectItem: could not convert {pluginItem.GetType().Name}");
            return pluginItem;
        }

        /// <summary>
        /// 通过运行时类型发现查找节点类型（assembly.GetTypes()）。
        /// 无 SDK 统一工厂，必须保留反射。
        /// </summary>
        private Type? FindNodeType(string typeName)
        {
            // 常见的 Kanzi 节点类型命名空间
            var namespaces = new[] {
                "Rightware.Kanzi",
                "Rightware.Kanzi.Presentation",
                "Rightware.Kanzi.Tool",
                "Kanzi"
            };

            // 首先尝试直接匹配（不区分大小写）
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var types = assembly.GetTypes();
                    // 首先尝试精确匹配
                    var type = types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                    if (type != null) return type;
                }
                catch { }
            }

            // 尝试在特定命名空间中查找
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var ns in namespaces)
                    {
                        var fullName = $"{ns}.{typeName}";
                        var type = assembly.GetType(fullName);
                        if (type != null) return type;

                        // 尝试其他可能的命名空间变体
                        var altName = $"{ns}.Logic.{typeName}";
                        type = assembly.GetType(altName);
                        if (type != null) return type;
                    }
                }
                catch { }
            }

            // 最后尝试模糊匹配
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetTypes()
                        .FirstOrDefault(t => t.Name.Contains(typeName) || (t.FullName?.Contains(typeName) ?? false));
                    if (type != null) return type;
                }
                catch { }
            }

            return null;
        }
    }
}
