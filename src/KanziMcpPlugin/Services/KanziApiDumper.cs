// KanziApiDumper.cs
//
// 文件作用: 运行时反射 Dumper — 导出 Kanzi Studio API 面到日志文件
// 关键类: KanziApiDumper（静态类）
// 主要职责:
//   1. 在 Kanzi Studio 插件初始化时，反射所有已加载的程序集
//   2. 将 KanziStudio/Project/ProjectItem 等核心类型的
//      属性、方法、事件写到 C:\temp\KanziApiDump.txt
//   3. 帮助开发者理解 Kanzi Plugin API 的真实结构（无官方文档时）
//   4. 扫描所有 Kanzi 相关程序集（PluginInterface、ApplicationCommon 等）
// 输出文件: C:\temp\KanziApiDump.txt（由 KanziService 调用）
// 依赖: Rightware.Kanzi.Studio.PluginInterface

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Rightware.Kanzi.Studio.PluginInterface;

namespace KanziMcpPlugin.Services
{
    /// <summary>
    /// 运行时反射 API Dumper — 在 Kanzi Studio 启动时将真实 API 面写到日志文件
    /// 改进版：移除 DeclaredOnly，扫描所有 Kanzi 相关程序集
    /// </summary>
    public static class KanziApiDumper
    {
        private static readonly string DumpPath = @"C:\temp\KanziApiDump.txt";

        public static void DumpApi(KanziStudio studio)
        {
            try
            {
                Directory.CreateDirectory(@"C:\temp");
                var sb = new StringBuilder();
                sb.AppendLine($"=== Kanzi Studio Plugin API Dump ===");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Studio Version: {studio.Version}");
                sb.AppendLine();

                // 1. 直接 dump KanziStudio 实例（含继承成员）
                DumpType(sb, "KanziStudio (instance)", studio.GetType(), studio, includeInherited: true);

                // 2. Dump KanziStudio 接口（通过 studio 对象找到接口类型）
                sb.AppendLine();
                sb.AppendLine("=== KanziStudio Interface (from studio object) ===");
                foreach (var iface in studio.GetType().GetInterfaces().OrderBy(i => i.Name))
                {
                    // 只 dump 名字中包含 Kanzi 的接口（过滤掉系统接口）
                    if (iface.Name.Contains("Kanzi") || iface.Namespace != null && iface.Namespace.Contains("Kanzi"))
                    {
                        DumpType(sb, $"Interface: {iface.Name}", iface, null, includeInherited: false);
                    }
                }

                // 3. 获取 ActiveProject 并 dump
                var activeProjectProp = studio.GetType().GetProperty("ActiveProject",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (activeProjectProp == null)
                {
                    // 尝试从接口中找
                    foreach (var iface in studio.GetType().GetInterfaces())
                    {
                        activeProjectProp = iface.GetProperty("ActiveProject",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (activeProjectProp != null) break;
                    }
                }

                if (activeProjectProp != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"=== ActiveProject Property ===");
                    sb.AppendLine($"  Property: {activeProjectProp.PropertyType.FullName} {activeProjectProp.Name}");
                    sb.AppendLine($"  DeclaringType: {activeProjectProp.DeclaringType?.FullName}");

                    try
                    {
                        var project = activeProjectProp.GetValue(studio);
                        if (project != null)
                        {
                            sb.AppendLine($"  Value: non-null ({project.GetType().FullName})");
                            DumpType(sb, "ActiveProject instance", project.GetType(), project, includeInherited: true);

                            // 尝试获取根节点 (/ 或空字符串)
                            TryDumpRootItem(sb, project);
                        }
                        else
                        {
                            sb.AppendLine("  Value: null");
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  Value: [ERROR reading: {ex.InnerException?.Message ?? ex.Message}]");
                    }
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine("=== ActiveProject Property: NOT FOUND ===");
                    // 列出所有属性名，帮助调试
                    sb.AppendLine("  All properties on studio object:");
                    foreach (var p in studio.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                    {
                        sb.AppendLine($"    {p.Name} ({p.PropertyType.Name})");
                    }
                }

                // 4. 扫描所有已加载的程序集，dump 所有 Kanzi 相关类型
                sb.AppendLine();
                sb.AppendLine("=== All Loaded Assemblies (Kanzi-related) ===");
                try
                {
                    var kanziAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a =>
                        {
                            var name = a.GetName().Name ?? "";
                            return name.Contains("Kanzi") || name.Contains("Rightware");
                        })
                        .OrderBy(a => a.GetName().Name);

                    foreach (var asm in kanziAssemblies)
                    {
                        sb.AppendLine($"  Assembly: {asm.GetName().Name} ({asm.Location})");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  Error: {ex.Message}");
                }

                // 5. Dump 关键接口类型：ProjectInterface, ProjectItemInterface
                sb.AppendLine();
                sb.AppendLine("=== Key Interface Types ===");
                DumpTypeFromAllAssemblies(sb, "ProjectInterface");
                DumpTypeFromAllAssemblies(sb, "ProjectItemInterface");
                DumpTypeFromAllAssemblies(sb, "Project");
                DumpTypeFromAllAssemblies(sb, "ProjectItem");

                // 6. 详细 dump 所有 Kanzi 相关程序集中的所有导出类型
                sb.AppendLine();
                sb.AppendLine("=== Detailed Type Dump (all Kanzi-related assemblies) ===");
                try
                {
                    var kanziAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a =>
                        {
                            var name = a.GetName().Name ?? "";
                            return name.Contains("Kanzi") || name.Contains("Rightware");
                        });

                    foreach (var asm in kanziAssemblies.OrderBy(a => a.GetName().Name))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"--- Assembly: {asm.GetName().Name} ---");
                        try
                        {
                            foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
                            {
                                DumpTypeShort(sb, t, includeInherited: false);
                            }
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine($"  Failed to enumerate types: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  Error: {ex.Message}");
                }

                File.WriteAllText(DumpPath, sb.ToString(), Encoding.UTF8);

                // 也写一条日志到主日志
                try
                {
                    File.AppendAllText(@"C:\temp\KanziMcpPlugin.log",
                        $"[{DateTime.Now:HH:mm:ss.fff}] [ApiDump] API dump written to {DumpPath} ({new FileInfo(DumpPath).Length} bytes){Environment.NewLine}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(@"C:\temp\KanziMcpPlugin.log",
                        $"[{DateTime.Now:HH:mm:ss.fff}] [ApiDump] FAILED: {ex.Message}{Environment.NewLine}");
                }
                catch { }
            }
        }

        /// <summary>
        /// 从所有已加载程序集中查找并 dump 指定名称的类型
        /// </summary>
        private static void DumpTypeFromAllAssemblies(StringBuilder sb, string typeName)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = asm.GetExportedTypes().FirstOrDefault(t =>
                        t.Name == typeName || t.FullName.EndsWith("." + typeName));
                    if (type != null)
                    {
                        sb.AppendLine();
                        DumpType(sb, $"{typeName} (from {asm.GetName().Name})", type, null, includeInherited: true);
                        return;
                    }
                }
                sb.AppendLine($"  {typeName}: NOT FOUND in any loaded assembly");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {typeName}: Error searching - {ex.Message}");
            }
        }

        /// <summary>
        /// 尝试获取并 dump 根节点
        /// </summary>
        private static void TryDumpRootItem(StringBuilder sb, object project)
        {
            var projectType = project.GetType();

            // 尝试 GetProjectItem 方法
            var getItemMethod = projectType.GetMethod("GetProjectItem", new[] { typeof(string) })
                ?? projectType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m => m.Name.Contains("GetProjectItem"));

            if (getItemMethod != null)
            {
                sb.AppendLine();
                sb.AppendLine($"=== GetProjectItem Method ===");
                sb.AppendLine($"  Method: {getItemMethod.ReturnType.Name} {getItemMethod.Name}({string.Join(", ", getItemMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");

                // 尝试用空字符串获取根
                foreach (var testPath in new[] { "", "/", "Screens", "Root" })
                {
                    try
                    {
                        var item = getItemMethod.Invoke(project, new object[] { testPath });
                        if (item != null)
                        {
                            sb.AppendLine();
                            sb.AppendLine($"=== ProjectItem (path: '{testPath}') ===");
                            DumpType(sb, $"ProjectItem ('{testPath}')", item.GetType(), item, includeInherited: true);
                            break;
                        }
                    }
                    catch { }
                }
            }

            // 也可以尝试 RootNode 或类似属性
            var rootProp = projectType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                .FirstOrDefault(p => p.Name.Contains("Root") || p.Name.Contains("Item") && p.PropertyType.Name.Contains("Project"));
            if (rootProp != null)
            {
                sb.AppendLine();
                sb.AppendLine($"=== Possible Root Property: {rootProp.Name} ===");
                try
                {
                    var root = rootProp.GetValue(project);
                    if (root != null)
                    {
                        DumpType(sb, rootProp.Name, root.GetType(), root, includeInherited: true);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  Failed to read: {ex.Message}");
                }
            }
        }

        private static void DumpType(StringBuilder sb, string label, Type type, object? instance, bool includeInherited)
        {
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (!includeInherited)
                bindingFlags |= BindingFlags.DeclaredOnly;

            sb.AppendLine($"=== {label} ===");
            sb.AppendLine($"  Type: {type.FullName}");
            sb.AppendLine($"  Assembly: {type.Assembly.GetName().Name}");
            if (type.BaseType != null && type.BaseType != typeof(object))
                sb.AppendLine($"  BaseType: {type.BaseType.FullName}");
            sb.AppendLine();

            // 属性
            sb.AppendLine("  Properties:");
            foreach (var prop in type.GetProperties(bindingFlags).OrderBy(p => p.Name))
            {
                var canRead = prop.CanRead ? "R" : "-";
                var canWrite = prop.CanWrite ? "W" : "-";
                var staticMod = prop.GetGetMethod()?.IsStatic == true ? "static " : "";

                // 尝试读取值
                string valueStr = "";
                if (instance != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        var val = prop.GetValue(instance);
                        valueStr = val != null
                            ? $" = {Truncate(val.ToString() ?? "null", 120)}"
                            : " = null";
                    }
                    catch (Exception ex)
                    {
                        valueStr = $" = [ERROR: {ex.InnerException?.Message ?? ex.Message}]";
                    }
                }

                sb.AppendLine($"    {staticMod}{canRead}{canWrite} {prop.PropertyType.FullName} {prop.Name}{valueStr}");
            }

            // 方法
            sb.AppendLine("  Methods:");
            foreach (var method in type.GetMethods(bindingFlags).OrderBy(m => m.Name))
            {
                if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_") ||
                    method.Name.StartsWith("add_") || method.Name.StartsWith("remove_"))
                    continue;

                var pars = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"));
                var staticMod = method.IsStatic ? "static " : "";
                sb.AppendLine($"    {staticMod}{method.ReturnType.FullName} {method.Name}({pars})");
            }

            // 事件
            var events = type.GetEvents(bindingFlags);
            if (events.Length > 0)
            {
                sb.AppendLine("  Events:");
                foreach (var evt in events.OrderBy(e => e.Name))
                {
                    sb.AppendLine($"    {evt.EventHandlerType.FullName} {evt.Name}");
                }
            }

            // 接口
            var interfaces = type.GetInterfaces();
            if (interfaces.Length > 0)
            {
                sb.AppendLine("  Implements:");
                foreach (var iface in interfaces.OrderBy(i => i.Name))
                {
                    sb.AppendLine($"    {iface.FullName}");
                }
            }
        }

        private static void DumpTypeShort(StringBuilder sb, Type type, bool includeInherited)
        {
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (!includeInherited)
                bindingFlags |= BindingFlags.DeclaredOnly;

            sb.AppendLine($"--- {type.FullName} ---");
            sb.Append($"  Kind: {(type.IsInterface ? "Interface" : type.IsEnum ? "Enum" : type.IsValueType ? "Struct" : "Class")}");
            if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
                sb.Append($"  Base: {type.BaseType.FullName}");
            sb.AppendLine();

            // 属性（只显示 Declared 的，即本类型直接定义的）
            var props = type.GetProperties(bindingFlags)
                .Where(p => includeInherited || p.DeclaringType == type);
            foreach (var prop in props.OrderBy(p => p.Name))
            {
                var staticMod = prop.GetGetMethod()?.IsStatic == true ? "static " : "";
                sb.AppendLine($"    {staticMod}{prop.PropertyType.Name} {prop.Name}");
            }

            // 方法（只显示 Declared 的）
            var methods = type.GetMethods(bindingFlags)
                .Where(m => includeInherited || m.DeclaringType == type)
                .Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") &&
                            !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_"));
            foreach (var method in methods.OrderBy(m => m.Name))
            {
                var pars = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                var staticMod = method.IsStatic ? "static " : "";
                sb.AppendLine($"    {staticMod}{method.ReturnType.Name} {method.Name}({pars})");
            }

            // 如果是接口，列出它定义的方法（包括来自继承接口的方法）
            if (type.IsInterface)
            {
                foreach (var m in type.GetMethods().OrderBy(m => m.Name))
                {
                    if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                    var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    sb.AppendLine($"    {m.ReturnType.Name} {m.Name}({pars})");
                }
            }
        }

        private static string Truncate(string s, int maxLen)
        {
            if (s == null) return "null";
            // 处理多行字符串
            s = s.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
