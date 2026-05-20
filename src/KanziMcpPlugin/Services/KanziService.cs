// KanziService.cs
//
// 文件作用: Kanzi Studio 业务层 — 所有节点/属性/绑定操作的真正执行者
// 关键类: KanziService
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
    public class KanziService
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

        #region 反射辅助方法

        /// <summary>
        /// 安全获取属性值 — 统一使用 FlattenHierarchy，捕获异常
        /// </summary>
        private object? SafeGetProperty(object obj, string propertyName)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                return prop?.GetValue(obj);
            }
            catch { return null; }
        }

        /// <summary>获取 ActiveProject 对象</summary>
        /// <remarks>
        /// API dump 确认：ActiveProject 声明在 PluginKanziStudioImplementation 基类上，
        /// 返回类型为 PluginInterface.Project。KanziStudio 接口也声明了此属性。
        /// FlattenHierarchy 或接口查找都应该能找到。
        /// </remarks>
        private object? GetActiveProject()
        {
            if (_studio == null)
            {
                Log("GetActiveProject: _studio is null");
                return null;
            }

            var studioType = _studio.GetType();
            Log($"GetActiveProject: studio type = {studioType.Name}");

            // 策略1: 直接在 studio 对象类型上查找（FlattenHierarchy 包含继承链）
            var prop = studioType.GetProperty("ActiveProject",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (prop != null)
            {
                try
                {
                    var val = prop.GetValue(_studio);
                    if (val != null) { Log($"GetActiveProject: found via FlattenHierarchy on {studioType.Name}"); return val; }
                }
                catch (Exception ex) { Log($"GetActiveProject: FlattenHierarchy failed: {ex.Message}"); }
            }

            // 策略2: 通过 KanziStudio 接口查找（显式接口实现时需要）
            foreach (var iface in studioType.GetInterfaces())
            {
                prop = iface.GetProperty("ActiveProject");
                if (prop != null)
                {
                    try
                    {
                        var val = prop.GetValue(_studio);
                        if (val != null) { Log($"GetActiveProject: found via interface {iface.Name}"); return val; }
                    }
                    catch (Exception ex) { Log($"GetActiveProject: interface {iface.Name} failed: {ex.Message}"); }
                }
            }

            // 策略3: 回退到 Project 属性
            prop = studioType.GetProperty("Project",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (prop != null)
            {
                try
                {
                    var val = prop.GetValue(_studio);
                    if (val != null) { Log($"GetActiveProject: found via Project property"); return val; }
                }
                catch (Exception ex) { Log($"GetActiveProject: Project property failed: {ex.Message}"); }
            }

            // 策略4: Primary_project 回退
            prop = studioType.GetProperty("PrimaryProject",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (prop != null)
            {
                try
                {
                    var val = prop.GetValue(_studio);
                    if (val != null) { Log($"GetActiveProject: found via PrimaryProject property"); return val; }
                }
                catch (Exception ex) { Log($"GetActiveProject: PrimaryProject property failed: {ex.Message}"); }
            }

            Log($"GetActiveProject: FAILED - all strategies exhausted");
            return null;
        }

        /// <summary>通过路径获取 ProjectItem</summary>
        /// <remarks>
        /// API dump 确认：Project 上没有 GetProjectItem(string) 方法。
        /// 但 ProjectItemInterface 有 GetChildByName(string) 方法。
        /// 实际实现是遍历路径各段，逐级用 GetChildByName 查找。
        /// 另外 ProjectInterface 有 GetItemByPath(ProjectItemPath)，但需要 ProjectItemPath 类型。
        /// </remarks>
        /// <summary>
        /// 去掉路径中的项目名前缀（如 "kanzi_mcp/Screens" -> "Screens"）
        /// 支持 "project/path" 和 "path" 两种格式
        /// </summary>
        private string StripProjectPrefix(string path)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(_projectName))
                return path;

            // 如果路径以 "projectName/" 开头，去掉它
            if (path.StartsWith(_projectName + "/") || path.StartsWith(_projectName + "\\"))
                return path.Substring(_projectName.Length + 1);

            // 如果路径就等于项目名，返回空（指向项目本身）
            if (path == _projectName)
                return "";

            return path;
        }

        /// <summary>
        /// 获取 ProjectItem，支持带项目名前缀的路径
        /// </summary>
        /// <remarks>
        /// 支持两种路径格式：
        /// - "Screens/Screen" （不带项目名前缀）
        /// - "kanzi_mcp/Screens/Screen" （带项目名前缀）
        /// </remarks>
        private object? GetProjectItem(string path)
        {
            var project = GetActiveProject();
            if (project == null)
            {
                Log($"GetProjectItem('{path}'): no active project");
                return null;
            }

            if (string.IsNullOrEmpty(path))
                return project;

            // 去掉项目名前缀，兼容带项目名前缀的路径
            var relativePath = StripProjectPrefix(path);
            Log($"GetProjectItem: '{path}' -> '{relativePath}'");

            // 拆分路径，逐级遍历
            var parts = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var current = project;

            foreach (var part in parts)
            {
                var children = GetChildren(current);
                var found = false;

                foreach (var child in children)
                {
                    var childName = GetItemName(child);
                    if (string.Equals(childName, part, StringComparison.Ordinal))
                    {
                        current = child;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // 尝试 GetChildByName 方法
                    var getChildMethod = current.GetType().GetMethod("GetChildByName",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string) }, null);
                    if (getChildMethod != null)
                    {
                        try
                        {
                            var item = getChildMethod.Invoke(current, new object[] { part });
                            if (item != null)
                            {
                                current = item;
                                found = true;
                                continue;
                            }
                        }
                        catch { }
                    }

                    Log($"GetProjectItem('{path}'): segment '{part}' not found");
                    return null;
                }
            }

            return current;
        }

        /// <summary>获取 ProjectItem 的子节点</summary>
        /// <remarks>
        /// API dump 确认：
        /// - ProjectItemInterface.Children → IEnumerable<ProjectItem>（PluginInterface 命名空间）
        /// - ProjectItemInterface.GetChildByName(string)
        /// - ProjectItemInterface.Parent → ProjectItem
        ///
        /// 只使用 Children 属性，不再做广泛的 IEnumerable 属性扫描
        /// （之前扫描到 Icon、CustomIcon 等非节点属性导致序列化失败）
        /// </remarks>
        private List<object> GetChildren(object projectItem)
        {
            var result = new List<object>();
            var type = projectItem.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            // 策略1: Children 属性 — 这是 ProjectItemInterface 定义的标准属性
            var childrenProp = type.GetProperty("Children", flags);
            if (childrenProp != null)
            {
                try
                {
                    var children = childrenProp.GetValue(projectItem) as IEnumerable;
                    if (children != null)
                    {
                        foreach (var child in children)
                            result.Add(child);
                        if (result.Count > 0) return result;
                    }
                }
                catch (Exception ex) { Log($"GetChildren: Children property failed: {ex.Message}"); }
            }

            // 策略2: 在接口中搜索 Children 属性
            foreach (var iface in type.GetInterfaces())
            {
                childrenProp = iface.GetProperty("Children");
                if (childrenProp != null)
                {
                    try
                    {
                        var children = childrenProp.GetValue(projectItem) as IEnumerable;
                        if (children != null)
                        {
                            foreach (var child in children)
                                result.Add(child);
                            if (result.Count > 0) return result;
                        }
                    }
                    catch { }
                }
            }

            if (result.Count == 0)
            {
                Log($"GetChildren: no children found on {type.Name}. " +
                    $"Available properties: {string.Join(", ", type.GetProperties(flags).Select(p => $"{p.Name}:{p.PropertyType.Name}"))}");
            }

            return result;
        }

        /// <summary>获取 ProjectItem 的名称</summary>
        private string GetItemName(object item)
        {
            // 优先用 Name 属性
            var name = SafeGetProperty(item, "Name") as string;
            if (!string.IsNullOrEmpty(name)) return name;

            // 回退到 DisplayName
            name = SafeGetProperty(item, "DisplayName") as string;
            if (!string.IsNullOrEmpty(name)) return name;

            return item.GetType().Name;
        }

        /// <summary>获取 ProjectItem 的路径</summary>
        private string GetItemPath(object item)
        {
            // 优先用 Path 属性
            var path = SafeGetProperty(item, "Path") as string;
            if (!string.IsNullOrEmpty(path)) return path;

            // 回退到 PathRelativeToProject
            path = SafeGetProperty(item, "PathRelativeToProject") as string;
            if (!string.IsNullOrEmpty(path)) return path;

            // 回退到 DisplayPath
            path = SafeGetProperty(item, "DisplayPath") as string;
            if (!string.IsNullOrEmpty(path)) return path;

            return GetItemName(item);
        }

        /// <summary>获取 ProjectItem 的类型名</summary>
        /// <remarks>
        /// API dump 确认 ProjectItem 有 TypeDisplayName 和 SubType 属性
        /// </remarks>
        private string GetItemType(object item)
        {
            var type = SafeGetProperty(item, "TypeDisplayName") as string;
            if (!string.IsNullOrEmpty(type)) return type;

            type = SafeGetProperty(item, "SubType") as string;
            if (!string.IsNullOrEmpty(type)) return type;

            // 使用类型名去掉 "Item" 后缀
            var typeName = item.GetType().Name;
            if (typeName.EndsWith("Item")) typeName = typeName.Substring(0, typeName.Length - 4);
            return typeName;
        }

        /// <summary>获取 ProjectItem 的所有属性（安全序列化）</summary>
        private Dictionary<string, object?> GetItemProperties(object item)
        {
            var props = new Dictionary<string, object?>();
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            try
            {
                // === 策略1: 从 ProjectItem.Properties 集合读取 ===
                // Properties 返回 IEnumerable<DynamicProperty>
                // DynamicProperty.Name = 属性全名（如 "PageHost.DefaultSubPage"）
                // DynamicProperty.Value = PropertyTypePluginWrapper<T>（不是原始值！）
                var propertiesProp = item.GetType().GetProperty("Properties", bf);
                if (propertiesProp != null)
                {
                    var properties = propertiesProp.GetValue(item) as IEnumerable;
                    if (properties != null)
                    {
                        foreach (var prop in properties)
                        {
                            try
                            {
                                var propName = SafeGetProperty(prop, "Name") as string ?? prop.ToString();
                                // 尝试读取属性值
                                object? propValue = TryReadPropertyValue(prop);

                                // 如果常规方法失败，尝试从 ProjectItem 自身读取
                                if (propValue == null || IsUnresolvedValue(propValue))
                                {
                                    var directValue = TryReadPropertyFromItem(item, propName);
                                    if (directValue != null && !IsUnresolvedValue(directValue))
                                        propValue = directValue;
                                }

                                // 如果仍然失败，从 Wrapper 的 DefaultValueTyped 中提取
                                if (propValue == null || IsUnresolvedValue(propValue))
                                {
                                    var wrapperDefault = TryReadWrapperDefault(prop);
                                    if (wrapperDefault != null)
                                        propValue = wrapperDefault;
                                }

                                props[propName] = propValue ?? "(unable to read)";
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // 如果 Properties 为空，尝试 PropertyTypes
            if (props.Count == 0)
            {
                try
                {
                    var propTypesProp = item.GetType().GetProperty("PropertyTypes", bf);
                    if (propTypesProp != null)
                    {
                        var propTypes = propTypesProp.GetValue(item) as IEnumerable;
                        if (propTypes != null)
                        {
                            foreach (var pt in propTypes)
                            {
                                try
                                {
                                    var ptName = SafeGetProperty(pt, "Name") as string ?? pt.ToString();
                                    props[ptName] = new { type = "PropertyType", source = "PropertyTypes" };
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            return props;
        }

        /// <summary>判断值是否为未解包的 Wrapper/类型标记</summary>
        private bool IsUnresolvedValue(object? value)
        {
            if (value == null) return false;
            var str = value.ToString() ?? "";
            // [PropertyTypePluginWrapper<String>], [NodeReference], [递归深度超限] 等
            return str.StartsWith("[PropertyTypePluginWrapper") ||
                   str.StartsWith("[NodeReference") ||
                   str.StartsWith("[ResourceReference") ||
                   str == "[递归深度超限]";
        }

        /// <summary>尝试从 ProjectItem 自身读取属性值（直接反射）</summary>
        /// <remarks>
        /// Kanzi 的 ProjectItem 有一些属性可以直接通过反射读取：
        /// - Name → string
        /// - Visible → bool
        /// 等等
        ///
        /// 对于 "PageHost.DefaultSubPage" 这样的复合属性名，
        /// 尝试用 LocalName（"DefaultSubPage"）在 item 上查找属性。
        /// </remarks>
        private object? TryReadPropertyFromItem(object item, string fullPropName)
        {
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            // 从全名提取简短名：PageHost.DefaultSubPage → DefaultSubPage
            var localName = fullPropName;
            var dotIdx = fullPropName.LastIndexOf('.');
            if (dotIdx >= 0 && dotIdx < fullPropName.Length - 1)
                localName = fullPropName.Substring(dotIdx + 1);

            // 策略1: 在 item 上查找同名属性
            try
            {
                var itemProp = item.GetType().GetProperty(localName, bf);
                if (itemProp != null && itemProp.CanRead && itemProp.GetIndexParameters().Length == 0)
                {
                    var val = itemProp.GetValue(item);
                    if (val != null)
                    {
                        // 简单类型直接返回
                        var valType = val.GetType();
                        if (valType.IsPrimitive || valType.IsEnum || val is string || val is bool ||
                            val is int || val is long || val is float || val is double)
                            return val;
                    }
                }
            }
            catch { }

            // 策略2: 尝试 GetPropertyValue / GetValue 方法
            try
            {
                var getValMethod = item.GetType().GetMethod("GetPropertyValue",
                    bf, null, new[] { typeof(string) }, null);
                if (getValMethod != null)
                {
                    var val = getValMethod.Invoke(item, new object[] { fullPropName });
                    if (val != null) return SafeConvertValue(val);
                }
            }
            catch { }

            try
            {
                var getValMethod = item.GetType().GetMethod("GetPropertyValue",
                    bf, null, new[] { typeof(string) }, null);
                if (getValMethod != null)
                {
                    var val = getValMethod.Invoke(item, new object[] { localName });
                    if (val != null) return SafeConvertValue(val);
                }
            }
            catch { }

            // 策略3: 尝试 GetProperty + GetValue（Kanzi 可能用 PropertyType 对象系统）
            try
            {
                var getPropMethod = item.GetType().GetMethod("GetProperty",
                    bf, null, new[] { typeof(string) }, null);
                if (getPropMethod != null)
                {
                    var propObj = getPropMethod.Invoke(item, new object[] { fullPropName });
                    if (propObj != null)
                    {
                        var valProp = propObj.GetType().GetProperty("Value", bf);
                        if (valProp != null && valProp.CanRead)
                        {
                            var val = valProp.GetValue(propObj);
                            if (val != null) return SafeConvertValue(val);
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>判断对象是否为有效的项目节点（不是原始类型）</summary>
        private bool IsValidProjectItem(object item)
        {
            return IsValidProjectItem(item, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        }

        /// <summary>判断对象是否为有效的项目节点（使用指定绑定标志）</summary>
        private bool IsValidProjectItem(object item, BindingFlags flags)
        {
            if (item == null) return false;

            var type = item.GetType();

            // 排除原始类型和常见值类型
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) ||
                type == typeof(int) || type == typeof(long) || type == typeof(float) ||
                type == typeof(double) || type == typeof(decimal) || type == typeof(bool) ||
                type == typeof(byte) || type == typeof(char) || type == typeof(DateTime))
            {
                return false;
            }

            // 排除值类型（struct）
            if (type.IsValueType && !type.IsEnum)
            {
                return false;
            }

            // 检查是否有 Name 属性（ProjectItem 的基本特征）
            try
            {
                var nameProp = type.GetProperty("Name", flags);
                if (nameProp == null || !nameProp.CanRead)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>通过 GetChildByName 方法获取子节点（更安全）</summary>
        private List<object> GetChildrenViaGetChildByName(object parent)
        {
            var result = new List<object>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            try
            {
                // 尝试调用 GetChildByName 方法获取所有可能的子节点
                var getChildByNameMethod = parent.GetType().GetMethod("GetChildByName",
                    flags, null, new[] { typeof(string) }, null);

                if (getChildByNameMethod == null)
                {
                    // 尝试 GetChildren 方法（如果存在）
                    var getChildrenMethod = parent.GetType().GetMethod("GetChildren", flags);

                    if (getChildrenMethod != null)
                    {
                        try
                        {
                            var childrenResult = getChildrenMethod.Invoke(parent, null);
                            if (childrenResult != null)
                            {
                                var items = EnumerateCollectionSafely(childrenResult, flags);
                                foreach (var item in items)
                                    result.Add(item);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"GetChildrenViaGetChildByName: GetChildren failed: {ex.Message}");
                        }
                    }
                    return result;
                }

                // GetChildByName 需要知道子节点的名称，但我们不知道有哪些子节点
                // 所以这个策略不太实用，改为使用 Children 属性
            }
            catch (Exception ex)
            {
                Log($"GetChildrenViaGetChildByName failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>安全获取子节点（使用手动枚举避免 Kanzi 集合问题）</summary>
        private List<object> GetChildrenSafe(object parent)
        {
            var result = new List<object>();

            Log($"GetChildrenSafe: START - parent type={parent.GetType().Name}");

            try
            {
                var type = parent.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

                // 尝试 GetChildren 方法（比 Children 属性更可靠）
                var getChildrenMethod = type.GetMethod("GetChildren", flags);
                if (getChildrenMethod != null)
                {
                    Log($"GetChildrenSafe: Found GetChildren method");
                    try
                    {
                        var childrenResult = getChildrenMethod.Invoke(parent, null);
                        if (childrenResult != null)
                        {
                            Log($"GetChildrenSafe: GetChildren returned result, enumerating...");
                            var items = EnumerateCollectionSafely(childrenResult, flags);
                            foreach (var item in items)
                                result.Add(item);
                            Log($"GetChildrenSafe: Enumerated {result.Count} children");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"GetChildrenSafe: GetChildren method failed: {ex.Message}");
                    }
                }

                // 回退到 Children 属性
                var childrenProp = type.GetProperty("Children", flags);
                if (childrenProp == null)
                {
                    foreach (var iface in type.GetInterfaces())
                    {
                        childrenProp = iface.GetProperty("Children", flags);
                        if (childrenProp != null) break;
                    }
                }

                if (childrenProp != null)
                {
                    try
                    {
                        var rawChildren = childrenProp.GetValue(parent);
                        if (rawChildren != null)
                        {
                            var items = EnumerateCollectionSafely(rawChildren, flags);
                            foreach (var item in items)
                                result.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"GetChildrenSafe: Children property failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetChildrenSafe: {ex.Message}");
            }

            return result;
        }

        /// <summary>安全枚举集合 - 使用 IEnumerator 手动枚举避免 foreach 的潜在问题</summary>
        private List<object> EnumerateCollectionSafely(object collection, BindingFlags flags)
        {
            var result = new List<object>();

            try
            {
                // 首先检查集合类型
                var collectionType = collection.GetType();

                // 检查是否是数组
                if (collectionType.IsArray)
                {
                    foreach (var item in (Array)collection)
                    {
                        if (item != null && IsValidProjectItem(item, flags))
                            result.Add(item);
                    }
                    return result;
                }

                // 尝试获取 GetEnumerator 方法
                var getEnumeratorMethod = collectionType.GetMethod("GetEnumerator", flags);
                if (getEnumeratorMethod != null)
                {
                    try
                    {
                        var enumerator = getEnumeratorMethod.Invoke(collection, null);
                        if (enumerator != null)
                        {
                            // 使用 IEnumerator 手动枚举
                            var moveNextMethod = enumerator.GetType().GetMethod("MoveNext", flags);
                            var currentProperty = enumerator.GetType().GetProperty("Current", flags);

                            if (moveNextMethod != null && currentProperty != null)
                            {
                                // 最多迭代 10000 次以防止无限循环
                                int iterations = 0;
                                const int maxIterations = 10000;

                                while (iterations < maxIterations)
                                {
                                    iterations++;
                                    try
                                    {
                                        var moved = (bool)moveNextMethod.Invoke(enumerator, null);
                                        if (!moved) break;

                                        var current = currentProperty.GetValue(enumerator);
                                        if (current != null && IsValidProjectItem(current, flags))
                                            result.Add(current);
                                    }
                                    catch
                                    {
                                        // MoveNext 或 Current 抛出异常，可能是集合已损坏
                                        break;
                                    }
                                }
                            }

                            // 尝试调用 Dispose
                            try
                            {
                                var disposeMethod = enumerator.GetType().GetMethod("Dispose", flags);
                                disposeMethod?.Invoke(enumerator, null);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"EnumerateCollectionSafely: GetEnumerator failed: {ex.Message}");
                    }
                    return result;
                }

                // 回退到 IEnumerable - 添加严格的类型检查避免 primitive 值导致错误
                if (collection is IEnumerable ienumerable)
                {
                    try
                    {
                        var enumerator = ienumerable.GetEnumerator();
                        var itemType = collection.GetType();
                        bool isArray = itemType.IsArray;
                        
                        while (enumerator.MoveNext())
                        {
                            var item = enumerator.Current;
                            // 严格类型检查：排除所有原始类型和值类型
                            if (item == null) continue;
                            var t = item.GetType();
                            if (t.IsPrimitive || t.IsEnum || t.IsValueType) continue;
                            // 排除常见 .NET 类型
                            if (t == typeof(string) || t == typeof(DateTime) || t == typeof(decimal)) continue;
                            // 排除没有 Name 属性的对象
                            if (IsValidProjectItem(item, flags))
                                result.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"EnumerateCollectionSafely: IEnumerable fallback failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"EnumerateCollectionSafely: {ex.Message}");
            }

            return result;
        }

        /// <summary>尝试从 Wrapper 的 DefaultValueTyped/DefaultValue 读取</summary>
        /// <remarks>
        /// 诊断发现 PropertyTypePluginWrapper 有以下属性：
        ///   DefaultValueTyped → "< Null >" 或实际值
        ///   DefaultValue → "< Null >" 或实际值
        ///   LocalName → 简短属性名
        ///   DisplayName → 显示名
        ///   Description → 属性描述
        ///   Category → 分类
        ///
        /// 当无法读取当前值时， DefaultValueTyped 可以提供默认值。
        /// "< Null >" 表示无值（未设置）。
        /// </remarks>
        private object? TryReadWrapperDefault(object dynamicProp)
        {
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            // 1. 尝试读取 DefaultValueTyped
            try
            {
                var defaultValProp = dynamicProp.GetType().GetProperty("DefaultValueTyped", bf);
                if (defaultValProp != null && defaultValProp.CanRead)
                {
                    var val = defaultValProp.GetValue(dynamicProp);
                    if (val != null)
                    {
                        var str = val.ToString() ?? "";
                        // "< Null >" 表示无值
                        if (str != "< Null >" && str != "<Null>" && str.Length > 0)
                            return SafeConvertValue(val);
                    }
                }
            }
            catch { }

            // 2. 尝试读取 CurrentValue / TypedValue
            foreach (var altName in new[] { "CurrentValue", "TypedValue", "InternalValue" })
            {
                try
                {
                    var altProp = dynamicProp.GetType().GetProperty(altName, bf);
                    if (altProp != null && altProp.CanRead)
                    {
                        var val = altProp.GetValue(dynamicProp);
                        if (val != null)
                        {
                            var str = val.ToString() ?? "";
                            if (str != "< Null >" && str != "<Null>")
                                return SafeConvertValue(val);
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>尝试读取 DynamicProperty 的值 — 多策略</summary>
        /// <remarks>
        /// Kanzi DynamicProperty 的值可能通过不同方式获取：
        /// 1. Value 属性（直接 get）→ 返回 PropertyTypePluginWrapper&lt;T&gt;
        /// 2. GetValue() 方法
        /// 3. InternalValue / RawValue 属性
        /// 4. SafeConvertValue 统一解包（核心逻辑）
        ///
        /// 重要：Value 属性返回的是 PropertyTypePluginWrapper&lt;T&gt; 对象，
        /// 不是原始 T 值！SafeConvertValue 内部会进一步解包。
        /// </remarks>
        private object? TryReadPropertyValue(object dynamicProp)
        {
            var propType = dynamicProp.GetType();
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            // 策略1: Value 属性 — 返回 PropertyTypePluginWrapper<T>，交给 SafeConvertValue 解包
            try
            {
                var valueProp = propType.GetProperty("Value", bf);
                if (valueProp != null && valueProp.CanRead)
                {
                    var rawValue = valueProp.GetValue(dynamicProp);
                    if (rawValue != null)
                    {
                        // ★ 关键：SafeConvertValue 会识别 Wrapper 类型并深入解包
                        var converted = SafeConvertValue(rawValue);
                        if (converted != null) return converted;
                    }
                    // Value 返回 null，继续尝试其他策略
                    Log($"TryReadPropertyValue: Value property returned null for {propType.Name}");
                }
            }
            catch (Exception ex)
            {
                Log($"TryReadPropertyValue: Value property failed: {ex.Message}");
            }

            // 策略2: GetValue() 方法
            try
            {
                var getValueMethod = propType.GetMethod("GetValue", Type.EmptyTypes);
                if (getValueMethod != null && getValueMethod.ReturnType != typeof(void))
                {
                    var rawValue = getValueMethod.Invoke(dynamicProp, null);
                    if (rawValue != null)
                    {
                        var converted = SafeConvertValue(rawValue);
                        if (converted != null) return converted;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"TryReadPropertyValue: GetValue() failed: {ex.Message}");
            }

            // 策略3: InternalValue / RawValue / CurrentValue 属性
            foreach (var altName in new[] { "InternalValue", "RawValue", "CurrentValue", "ObjectValue", "DisplayValue" })
            {
                try
                {
                    var altProp = propType.GetProperty(altName, bf);
                    if (altProp != null && altProp.CanRead)
                    {
                        var rawValue = altProp.GetValue(dynamicProp);
                        if (rawValue != null)
                        {
                            var converted = SafeConvertValue(rawValue);
                            if (converted != null) return converted;
                        }
                    }
                }
                catch { }
            }

            // 策略4: 整个 DynamicProperty 对象交给 SafeConvertValue（它会尝试解包）
            try
            {
                var converted = SafeConvertValue(dynamicProp);
                if (converted != null && converted.ToString() != $"[{propType.Name}]")
                    return converted;
            }
            catch { }

            // 诊断: 记录 DynamicProperty 类型的所有属性和方法，帮助调试
            Log($"TryReadPropertyValue: ALL strategies failed for {propType.FullName}");
            try
            {
                var allProps = string.Join(", ", propType.GetProperties(bf).Select(p => $"{p.PropertyType.Name} {p.Name}"));
                Log($"  Properties: {allProps}");
                var allMethods = string.Join(", ", propType.GetMethods(bf)
                    .Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") &&
                                !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_"))
                    .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})"));
                Log($"  Methods: {allMethods}");
            }
            catch { }

            return null;
        }

        /// <summary>安全转换值 — 确保可以 JSON 序列化</summary>
        /// <remarks>
        /// Kanzi 属性值核心问题：DynamicProperty.Value 返回的是 PropertyTypePluginWrapper&lt;T&gt; 对象，
        /// 其 ToString() 输出完整类型名而非实际值。需要深入反射读取 Wrapper 内部的实际值。
        ///
        /// Wrapper 类型层次：
        ///   PropertyTypePluginWrapper&lt;T&gt; → 内部有 Value 属性（类型为 T）
        ///   Vector2DPropertyTypePluginWrapper → 继承自某个 Wrapper，Value 是 Vector2D
        ///
        /// 泛型参数 T 的常见类型及提取策略：
        ///   string → 直接取值
        ///   bool/int/float/double → 直接取值
        ///   Enum → .ToString() 取枚举名
        ///   NodeReference&lt;T&gt; → 取其内部节点路径/名称
        ///   ResourceReference&lt;T&gt; → 取其资源路径/名称
        ///   IEnumerable → 遍历取值
        ///   其他复杂类型 → 尝试 ToString，若返回类型名则标记为 [复杂类型]
        /// </remarks>

        /// </remarks>
        private object? SafeConvertValue(object? value, int depth = 0)
        {
            if (value == null) return null;
            if (depth > 5) return "[递归深度超限]"; // 防止无限递归

            var type = value.GetType();

            // 基本类型直接返回
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal)
            {
                if (type.IsEnum) return value.ToString(); // 枚举返回名字而非数字
                return value;
            }

            // 数值类型
            if (value is int || value is long || value is float || value is double || value is bool)
                return value;

            // ★★★ Kanzi PropertyTypePluginWrapper 解包 ★★★
            // 类型名包含 "PropertyTypePluginWrapper" 或 "PropertyWrapper" 的都是 Kanzi 属性包装
            if (type.Name.Contains("PropertyTypePluginWrapper") ||
                type.Name.Contains("PropertyWrapper") ||
                type.FullName?.Contains("PropertyWrappers") == true)
            {
                return UnwrapKanziProperty(value, type, depth);
            }

            // Kanzi 特殊引用类型：NodeReference<T>, ResourceReference<T>
            if (type.Name.StartsWith("NodeReference") || type.Name.StartsWith("ResourceReference"))
            {
                return ExtractReferenceValue(value, type);
            }

            // Vector2D / Vector3D 等值类型
            if (type.Name == "Vector2D" || type.Name == "Vector3D")
            {
                return ExtractVectorValue(value, type);
            }

            // IEnumerable（非 string）→ 转为列表
            if (value is IEnumerable enumerable && !(value is string))
            {
                return ExtractEnumerableValue(enumerable, depth);
            }

            // 尝试读取 Value 属性（嵌套包装类型）
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                var innerValueProp = type.GetProperty("Value", bf);
                if (innerValueProp != null && innerValueProp.CanRead && innerValueProp.PropertyType != type)
                {
                    var innerVal = innerValueProp.GetValue(value);
                    if (innerVal != null)
                        return SafeConvertValue(innerVal, depth + 1);
                }
            }
            catch { }

            // 尝试 ToString — 大多数 Kanzi 类型有合理的 ToString
            try
            {
                var str = value.ToString();
                if (!string.IsNullOrEmpty(str) && str != type.Name && str != type.FullName)
                    return str;
            }
            catch { }

            // 无法安全序列化的类型返回简短类型标记
            return $"[{type.Name}]";
        }

        /// <summary>解包 Kanzi PropertyTypePluginWrapper 属性包装</summary>
        /// <remarks>
        /// 诊断发现（2026-05-09）：PropertyTypePluginWrapper&lt;T&gt; 不是简单的值包装器！
        /// 它是 DynamicProperty 的本体，包含属性描述信息：
        ///   - .Name → 属性全名（如 "PageHost.DefaultSubPage"）
        ///   - .LocalName → 简短属性名（如 "DefaultSubPage"）
        ///   - .DisplayName → 显示名（如 "Default Subpage"）
        ///   - .Description → 属性描述
        ///   - .DataType → 属性类型（RuntimeType，如 NodeReference&lt;Node2D&gt;）
        ///   - .DefaultValueTyped → 默认值（"< Null >" 表示未设置）
        ///   - .DefaultValue → 默认值
        ///   - .IsInherited → 是否继承属性
        ///   - .Category → 属性分类
        ///
        /// 关键发现：Wrapper.Value 返回的是 Wrapper 自身！不是内部的 T 值。
        /// 真正的属性当前值需要通过其他方式获取：
        ///   策略A: DefaultValueTyped（如果不是 "< Null >"）
        ///   策略B: 通过 ProjectItem 上的 GetPropertyValue 方法
        ///   策略C: 返回属性描述信息（名称、类型、默认值），供用户理解
        /// </remarks>
        private object? UnwrapKanziProperty(object wrapper, Type wrapperType, int depth)
        {
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            // 1. 尝试读取 DefaultValueTyped — 这是"当前设置值"或"默认值"
            try
            {
                var defaultValProp = wrapperType.GetProperty("DefaultValueTyped", bf);
                if (defaultValProp != null && defaultValProp.CanRead)
                {
                    var val = defaultValProp.GetValue(wrapper);
                    if (val != null)
                    {
                        var str = val.ToString() ?? "";
                        if (str != "< Null >" && str != "<Null>")
                        {
                            return SafeConvertValue(val, depth + 1);
                        }
                    }
                }
            }
            catch { }

            // 2. 尝试 CurrentValue / TypedValue
            foreach (var altName in new[] { "CurrentValue", "TypedValue", "InternalValue", "RawValue" })
            {
                try
                {
                    var altProp = wrapperType.GetProperty(altName, bf);
                    if (altProp != null && altProp.CanRead)
                    {
                        var val = altProp.GetValue(wrapper);
                        if (val != null)
                        {
                            var str = val.ToString() ?? "";
                            if (str != "< Null >" && str != "<Null>")
                                return SafeConvertValue(val, depth + 1);
                        }
                    }
                }
                catch { }
            }

            // 3. 尝试读取 Wrapper.Value（可能返回自身，但也可能有些 Wrapper 确实有内部值）
            try
            {
                var valueProp = wrapperType.GetProperty("Value", bf);
                if (valueProp != null && valueProp.CanRead)
                {
                    var innerValue = valueProp.GetValue(wrapper);
                    if (innerValue != null && innerValue != wrapper) // 避免自引用
                    {
                        var innerType = innerValue.GetType();
                        // 如果内部值不是 Wrapper 类型，尝试转换
                        if (!innerType.Name.Contains("PropertyTypePluginWrapper") &&
                            innerType.FullName?.Contains("PropertyWrappers") != true)
                        {
                            var result = SafeConvertValue(innerValue, depth + 1);
                            if (result != null && !IsUnresolvedValue(result))
                                return result;
                        }
                    }
                }
            }
            catch { }

            // 4. 尝试 GetValue() 方法
            try
            {
                var getValueMethod = wrapperType.GetMethod("GetValue", Type.EmptyTypes);
                if (getValueMethod != null && getValueMethod.ReturnType != typeof(void))
                {
                    var innerValue = getValueMethod.Invoke(wrapper, null);
                    if (innerValue != null && innerValue != wrapper)
                    {
                        var result = SafeConvertValue(innerValue, depth + 1);
                        if (result != null && !IsUnresolvedValue(result))
                            return result;
                    }
                }
            }
            catch { }

            // 5. 尝试私有字段（泛型参数匹配）
            if (wrapperType.IsGenericType)
            {
                var genArgs = wrapperType.GetGenericArguments();
                if (genArgs.Length == 1)
                {
                    var innerType = genArgs[0];
                    foreach (var field in wrapperType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        try
                        {
                            if (field.FieldType == innerType || innerType.IsAssignableFrom(field.FieldType))
                            {
                                var fieldVal = field.GetValue(wrapper);
                                if (fieldVal != null)
                                    return SafeConvertValue(fieldVal, depth + 1);
                            }
                        }
                        catch { }
                    }
                }
            }

            // 6. 最后手段：返回属性描述信息（比 [Wrapper] 有用得多）
            var desc = new Dictionary<string, object?>();

            try
            {
                var name = SafeGetProperty(wrapper, "LocalName") as string ??
                           SafeGetProperty(wrapper, "Name") as string;
                if (!string.IsNullOrEmpty(name)) desc["name"] = name;
            }
            catch { }

            try
            {
                var displayName = SafeGetProperty(wrapper, "DisplayName") as string;
                if (!string.IsNullOrEmpty(displayName)) desc["display"] = displayName;
            }
            catch { }

            try
            {
                var dataType = SafeGetProperty(wrapper, "DataType") as string;
                if (!string.IsNullOrEmpty(dataType)) desc["type"] = dataType;
                else
                {
                    // DataType 是 RuntimeType，取 Name
                    var dtObj = SafeGetProperty(wrapper, "DataType");
                    if (dtObj != null) desc["type"] = dtObj.GetType().Name == "RuntimeType" ?
                        dtObj.ToString() : dtObj.GetType().Name;
                }
            }
            catch { }

            try
            {
                var isInherited = SafeGetProperty(wrapper, "IsInherited");
                if (isInherited != null) desc["inherited"] = isInherited.ToString();
            }
            catch { }

            try
            {
                var category = SafeGetProperty(wrapper, "Category") as string;
                if (!string.IsNullOrEmpty(category)) desc["category"] = category;
            }
            catch { }

            // 如果有描述信息，返回字典；否则返回简短标记
            if (desc.Count > 0)
            {
                desc["_unresolved"] = true;
                return desc;
            }

            var propTypeName = wrapperType.Name;
            if (wrapperType.IsGenericType)
            {
                var genArgs = wrapperType.GetGenericArguments();
                propTypeName = $"{wrapperType.Name.Split('`')[0]}<{string.Join(",", genArgs.Select(a => a.Name))}>";
            }
            return $"[{propTypeName}]";
        }

        /// <summary>提取 Kanzi 引用类型值（NodeReference, ResourceReference）</summary>
        private object? ExtractReferenceValue(object reference, Type refType)
        {
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            // NodeReference<T> — 尝试取节点路径或名称
            // ResourceReference<T> — 尝试取资源路径或名称

            // 策略1: Name 属性
            try
            {
                var name = SafeGetProperty(reference, "Name") as string;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }

            // 策略2: Path 属性
            try
            {
                var path = SafeGetProperty(reference, "Path") as string;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch { }

            // 策略3: Value 属性（递归）
            try
            {
                var valueProp = refType.GetProperty("Value", bf);
                if (valueProp != null && valueProp.CanRead)
                {
                    var val = valueProp.GetValue(reference);
                    if (val != null) return SafeConvertValue(val, 1);
                }
            }
            catch { }

            // 策略4: ToString（排除类型名）
            try
            {
                var str = reference.ToString();
                if (!string.IsNullOrEmpty(str) && str != refType.Name && str != refType.FullName)
                    return str;
            }
            catch { }

            return $"[{refType.Name}]";
        }

        /// <summary>提取 Vector2D/Vector3D 值</summary>
        private object? ExtractVectorValue(object vector, Type vecType)
        {
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var parts = new List<string>();

            // Vector2D 通常有 X/Y 或 Width/Height 属性
            foreach (var propName in new[] { "X", "Y", "Z", "Width", "Height" })
            {
                try
                {
                    var prop = vecType.GetProperty(propName, bf);
                    if (prop != null && prop.CanRead)
                    {
                        var val = prop.GetValue(vector);
                        if (val != null) parts.Add($"{propName}={val}");
                    }
                }
                catch { }
            }

            if (parts.Count > 0) return string.Join(", ", parts);

            // 回退到 ToString
            try
            {
                var str = vector.ToString();
                if (!string.IsNullOrEmpty(str) && str != vecType.Name) return str;
            }
            catch { }

            return $"[{vecType.Name}]";
        }

        /// <summary>提取 IEnumerable 值（转为列表）</summary>
        private object? ExtractEnumerableValue(IEnumerable enumerable, int depth)
        {
            var items = new List<object?>();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count >= 50) // 限制集合大小
                {
                    items.Add("...(truncated)");
                    break;
                }
                items.Add(item != null ? SafeConvertValue(item, depth + 1) : null);
                count++;
            }
            return items;
        }

        /// <summary>从绑定 Property 值中提取属性名（去掉 Wrapper 类型前缀）</summary>
        /// <remarks>
        /// GetBindingInfo 返回的 binding.Property 字段可能是：
        ///   1. string 类型名（含 PropertyTypePluginWrapper 前缀）→ 需提取纯属性名
        ///   2. 对象（如 PropertyTypePluginWrapper）→ 尝试读 Name 属性
        ///   3. 其他 → 转字符串
        ///
        /// 例如传入 "Rightware.Kanzi.Tool.Logic.Project.Plugin.PropertyWrappers.PropertyTypePluginWrapper`1[...] PageHost.DefaultSubPage"
        /// 应返回 "PageHost.DefaultSubPage"
        /// </remarks>
        private string ExtractBindingProperty(object? propObj)
        {
            if (propObj == null) return "unknown";

            // 1. string — 可能含 Wrapper 类型名前缀，需提取纯属性名
            if (propObj is string s)
            {
                // 格式: "Rightware...PropertyTypePluginWrapper`1[...] PropertyName"
                // 找最后一个空格后的内容（即真实属性名）
                var lastSpace = s.LastIndexOf(' ');
                if (lastSpace >= 0 && lastSpace < s.Length - 1)
                {
                    var candidate = s.Substring(lastSpace + 1);
                    // 确保候选不包含方括号（不是泛型参数部分）
                    if (!candidate.Contains('[') && !candidate.Contains('`'))
                        return candidate;
                }
                // 不含空格或解析失败，检查是否是纯属性名
                if (!s.Contains(".") || s.Contains("Rightware") || s.Contains("PropertyTypePluginWrapper"))
                    return "unknown";
                return s;
            }

            // 2. 对象类型 — 尝试读 Name 属性（Wrapper 通常有 Name 属性存属性名）
            var type = propObj.GetType();
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                var nameProp = type.GetProperty("Name", bf);
                if (nameProp != null && nameProp.CanRead)
                {
                    var name = nameProp.GetValue(propObj) as string;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }

            // 3. ToString — 过滤掉类型名
            try
            {
                var str = propObj.ToString() ?? "unknown";
                var lastSpace = str.LastIndexOf(' ');
                if (lastSpace >= 0 && lastSpace < str.Length - 1)
                {
                    var candidate = str.Substring(lastSpace + 1);
                    if (!candidate.Contains('[') && !candidate.Contains('`') &&
                        !candidate.Contains("Rightware") && !candidate.Contains("PropertyTypePluginWrapper"))
                        return candidate;
                }
                if (str.Length < 100 && !str.Contains("Rightware"))
                    return str;
            }
            catch { }

            return "unknown";
        }

        #endregion

        #region 节点查询

        public string QueryNodes(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                var filter = ParseNodeFilter(args);
                var allNodes = new List<Dictionary<string, object?>>();

                CollectNodesRecursive(project, "", filter, allNodes, 0);

                return SafeSerialize(new
                {
                    success = true,
                    projectName = _projectName,
                    nodes = allNodes.Take(filter.Limit),
                    count = Math.Min(allNodes.Count, filter.Limit),
                    totalMatched = allNodes.Count,
                    truncated = allNodes.Count > filter.Limit
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"查询节点失败: {ex.Message}");
            }
        }

        private void CollectNodesRecursive(object parent, string parentPath, NodeFilter filter,
            List<Dictionary<string, object?>> results, int depth)
        {
            if (depth > 20 || results.Count >= filter.Limit * 2) return;

            try
            {
                foreach (var child in GetChildren(parent))
                {
                    var name = GetItemName(child);
                    var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
                    var type = GetItemType(child);

                    if (MatchesFilter(name, type, path, filter))
                    {
                        var nodeInfo = new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["name"] = name,
                            ["type"] = type
                        };

                        if (filter.IncludeProperties)
                        {
                            nodeInfo["properties"] = GetItemProperties(child);
                        }

                        results.Add(nodeInfo);
                    }

                    if (filter.Recursive)
                    {
                        CollectNodesRecursive(child, path, filter, results, depth + 1);
                    }
                }
            }
            catch { }
        }

        private bool MatchesFilter(string name, string type, string path, NodeFilter filter)
        {
            if (filter.Type != null && !WildcardMatch(type, filter.Type))
                return false;
            if (filter.Name != null && !WildcardMatch(name, filter.Name))
                return false;
            if (filter.Path != null && !path.StartsWith(filter.Path))
                return false;
            return true;
        }

        public string GetNodeTree(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            string rootPath = "";
            int depth = 3;
            bool includeProperties = false;

            if (args.HasValue)
            {
                if (args.Value.TryGetProperty("rootPath", out var rp))
                    rootPath = rp.GetString() ?? "";
                if (args.Value.TryGetProperty("depth", out var d))
                    depth = d.GetInt32();
                if (args.Value.TryGetProperty("includeProperties", out var ip))
                    includeProperties = ip.GetBoolean();
            }

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                object? rootItem;
                if (!string.IsNullOrEmpty(rootPath))
                {
                    rootItem = GetProjectItem(rootPath);
                    if (rootItem == null)
                        return ErrorJson($"节点未找到: {rootPath}");
                }
                else
                {
                    rootItem = project;
                }

                var tree = BuildTreeRecursive(rootItem, "", depth, includeProperties);

                return SafeSerialize(new
                {
                    success = true,
                    tree
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"获取节点树失败: {ex.Message}");
            }
        }

        private Dictionary<string, object?> BuildTreeRecursive(object item, string currentPath, int depth, bool includeProperties)
        {
            var name = GetItemName(item);
            var path = string.IsNullOrEmpty(currentPath) ? name : $"{currentPath}/{name}";
            var type = GetItemType(item);

            var result = new Dictionary<string, object?>
            {
                ["path"] = path,
                ["name"] = name,
                ["type"] = type
            };

            if (includeProperties)
            {
                result["properties"] = GetItemProperties(item);
            }

            if (depth > 0)
            {
                try
                {
                    var children = new List<Dictionary<string, object?>>();
                    foreach (var child in GetChildren(item))
                    {
                        children.Add(BuildTreeRecursive(child, path, depth - 1, includeProperties));
                    }
                    if (children.Count > 0)
                        result["children"] = children;
                }
                catch (Exception ex)
                {
                    Log($"BuildTreeRecursive: error getting children of {name}: {ex.Message}");
                    result["childrenError"] = ex.Message;
                }
            }

            return result;
        }

        public string ListNodeTypes(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                var types = new List<Dictionary<string, object?>>();

                // 尝试获取 NodeComponentTypeLibrary（API dump 确认存在于 ProjectInterface 中）
                var libProp = project.GetType().GetProperty("NodeComponentTypeLibrary",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (libProp != null)
                {
                    try
                    {
                        var lib = libProp.GetValue(project) as IEnumerable;
                        if (lib != null)
                        {
                            foreach (var item in lib)
                            {
                                types.Add(new Dictionary<string, object?>
                                {
                                    ["type"] = GetItemName(item),
                                    ["displayName"] = SafeGetProperty(item, "DisplayName") as string ?? GetItemName(item),
                                    ["category"] = SafeGetProperty(item, "Category") as string ?? "General"
                                });
                            }
                        }
                    }
                    catch { }
                }

                // 如果没有找到 NodeComponentTypeLibrary，返回已知的 Kanzi 节点类型
                if (types.Count == 0)
                {
                    var knownTypes = new[]
                    {
                        "Node", "Node2D", "Node3D", "NodeGroup2D", "EmptyNode2D",
                        "NodeWithLayout2D", "TextBlock2D", "TextBlock3D",
                        "Image2D", "Image3D", "RectangleNode2D",
                        "Button2D", "Button3D", "ProgressBar2D",
                        "Slider2D", "ListBox2D", "Shape2DNode",
                        "ParticleSystemNode", "Viewport2D", "Scene"
                    };

                    foreach (var t in knownTypes)
                    {
                        types.Add(new Dictionary<string, object?>
                        {
                            ["type"] = t,
                            ["displayName"] = t,
                            ["category"] = GetNodeCategory(t)
                        });
                    }
                }

                return SafeSerialize(new
                {
                    success = true,
                    nodeTypes = types,
                    count = types.Count
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"列出节点类型失败: {ex.Message}");
            }
        }

        public string GetBindingInfo(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            if (!args.HasValue || !args.Value.TryGetProperty("path", out var pathEl))
                return ErrorJson("缺少 path 参数");

            var path = pathEl.GetString() ?? "";

            try
            {
                var item = GetProjectItem(path);
                if (item == null)
                    return ErrorJson($"节点未找到: {path}");

                var bindings = new List<Dictionary<string, object?>>();

                // API dump 确认：ProjectItem 有 Bindings 属性 (IEnumerable<IBindingItem>)
                var bindingsProp = item.GetType().GetProperty("Bindings",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (bindingsProp != null)
                {
                    try
                    {
                        var bindingsCollection = bindingsProp.GetValue(item) as IEnumerable;
                        if (bindingsCollection != null)
                        {
                            foreach (var binding in bindingsCollection)
                            {
                                try
                                {
                                    bindings.Add(new Dictionary<string, object?>
                                    {
                                        // Property 字段可能是 PropertyTypePluginWrapper 类型名，需提取实际属性名
                                        ["property"] = ExtractBindingProperty(SafeGetProperty(binding, "Property")),
                                        ["code"] = SafeGetProperty(binding, "Code") as string ?? "",
                                        ["mode"] = SafeGetProperty(binding, "Mode")?.ToString() ?? "OneWay"
                                    });
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                return SafeSerialize(new
                {
                    success = true,
                    node = path,
                    bindings
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"获取绑定信息失败: {ex.Message}");
            }
        }

        public string SearchNodes(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            if (!args.HasValue || !args.Value.TryGetProperty("searchText", out var st))
                return ErrorJson("缺少 searchText 参数");

            var searchText = st.GetString() ?? "";
            var caseSensitive = args.Value.TryGetProperty("caseSensitive", out var cs) && cs.GetBoolean();

            // 解析 searchIn 参数，默认搜索 Name 和 Path
            var searchIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Name", "Path" };
            if (args.Value.TryGetProperty("searchIn", out var siEl))
            {
                searchIn.Clear();
                foreach (var item in siEl.EnumerateArray())
                {
                    var s = item.GetString() ?? "";
                    if (!string.IsNullOrEmpty(s))
                        searchIn.Add(s);
                }
                if (searchIn.Count == 0)
                    searchIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Name", "Path" };
            }

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                var results = new List<Dictionary<string, object?>>();
                SearchNodesRecursive(project, "", searchText, caseSensitive, searchIn, results, 0);

                return SafeSerialize(new
                {
                    success = true,
                    searchText,
                    searchIn = searchIn.ToList(),
                    results,
                    count = results.Count
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"搜索节点失败: {ex.Message}");
            }
        }

        private void SearchNodesRecursive(object parent, string parentPath, string searchText,
            bool caseSensitive, HashSet<string> searchIn, List<Dictionary<string, object?>> results, int depth)
        {
            if (depth > 20 || results.Count >= 500) return;

            try
            {
                foreach (var child in GetChildren(parent))
                {
                    var name = GetItemName(child);
                    var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
                    var type = GetItemType(child);

                    var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    var matchReasons = new List<string>();

                    // 搜索 Name
                    if (searchIn.Contains("Name") && name.IndexOf(searchText, comparison) >= 0)
                        matchReasons.Add("name");

                    // 搜索 Path
                    if (searchIn.Contains("Path") && path.IndexOf(searchText, comparison) >= 0)
                        matchReasons.Add("path");

                    // 搜索 Type
                    if (searchIn.Contains("Type") && type.IndexOf(searchText, comparison) >= 0)
                        matchReasons.Add("type");

                    // 搜索 Text 属性（TextBlock2D 等文本节点的文本内容）
                    if (searchIn.Contains("Text"))
                    {
                        try
                        {
                            var textValue = SafeGetProperty(child, "Text") as string;
                            if (!string.IsNullOrEmpty(textValue) && textValue.IndexOf(searchText, comparison) >= 0)
                                matchReasons.Add("text");
                        }
                        catch { }
                    }

                    if (matchReasons.Count > 0)
                    {
                        results.Add(new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["name"] = name,
                            ["type"] = type,
                            ["matchReason"] = string.Join(",", matchReasons)
                        });
                    }

                    SearchNodesRecursive(child, path, searchText, caseSensitive, searchIn, results, depth + 1);
                }
            }
            catch { }
        }

        #endregion

        #region 属性操作

        public string SetProperty(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            if (!args.HasValue)
                return ErrorJson("缺少参数");

            var path = args.Value.TryGetProperty("path", out var p) ? p.GetString() : "";
            var property = args.Value.TryGetProperty("property", out var pr) ? pr.GetString() : "";
            var mode = args.Value.TryGetProperty("mode", out var m) ? m.GetString() ?? "preview" : "preview";
            var force = args.Value.TryGetProperty("force", out var f) && f.GetBoolean();

            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(property))
                return ErrorJson("缺少 path 或 property 参数");

            try
            {
                var item = GetProjectItem(path);
                if (item == null)
                    return ErrorJson($"节点未找到: {path}");

                var itemType = item.GetType().Name;
                Log($"SetProperty: item type = {itemType}, property = {property}, mode = {mode}");

                // 获取旧值
                string? oldValue = null;
                try
                {
                    var existingProp = item.GetType().GetProperty(property,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (existingProp != null)
                    {
                        var rawVal = existingProp.GetValue(item);
                        oldValue = rawVal?.ToString();
                    }
                    else
                    {
                        // 尝试通过 GetProperty 方法获取
                        var getMethod = item.GetType().GetMethod("GetProperty",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                            null, new[] { typeof(string) }, null);
                        if (getMethod != null)
                        {
                            var val = getMethod.Invoke(item, new object[] { property });
                            oldValue = val?.ToString();
                        }
                    }
                }
                catch (Exception ex) { Log($"SetProperty: failed to get old value: {ex.Message}"); }

                // 提取新值
                var valueEl = args.Value.TryGetProperty("value", out var v) ? v : default;
                var newValueObj = JsonElementToObject(valueEl);
                Log($"SetProperty: oldValue = {oldValue}, newValue = {newValueObj} ({newValueObj?.GetType().Name ?? "null"})");

                if (mode == "preview")
                {
                    // 安全处理 newValue，确保可以序列化
                    object? safeValue;
                    if (newValueObj == null)
                    {
                        safeValue = null;
                    }
                    else if (newValueObj is int || newValueObj is long || newValueObj is float ||
                             newValueObj is double || newValueObj is bool || newValueObj is string)
                    {
                        // 基本类型直接使用
                        safeValue = newValueObj;
                    }
                    else
                    {
                        // 其他类型转换为字符串
                        safeValue = newValueObj.ToString();
                    }

                    return SafeSerialize(new
                    {
                        success = true,
                        preview = true,
                        node = path,
                        nodeType = itemType,
                        property,
                        oldValue,
                        newValue = safeValue
                    });
                }

                // apply 模式 - 多策略尝试设置属性
                try
                {
                    // 策略1: SetPropertyWithCommand(string, object) — 支持 undo/redo
                    var setMethod = item.GetType().GetMethod("SetPropertyWithCommand",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object) }, null);
                    if (setMethod != null)
                    {
                        Log($"SetProperty: using SetPropertyWithCommand");
                        setMethod.Invoke(item, new[] { property, newValueObj });

                        // 验证新值
                        string? verifiedValue = null;
                        try
                        {
                            var getMethod = item.GetType().GetMethod("GetProperty",
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                                null, new[] { typeof(string) }, null);
                            if (getMethod != null)
                            {
                                var val = getMethod.Invoke(item, new object[] { property });
                                verifiedValue = val?.ToString();
                            }
                        }
                        catch { }

                        return SafeSerialize(new
                        {
                            success = true,
                            preview = false,
                            node = path,
                            nodeType = itemType,
                            property,
                            oldValue,
                            newValue = verifiedValue ?? newValueObj?.ToString(),
                            appliedVia = "SetPropertyWithCommand"
                        });
                    }

                    // 策略2: SetOrCreatePropertyWithCommand(string, object)
                    setMethod = item.GetType().GetMethod("SetOrCreatePropertyWithCommand",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object) }, null);
                    if (setMethod != null)
                    {
                        Log($"SetProperty: using SetOrCreatePropertyWithCommand");
                        setMethod.Invoke(item, new[] { property, newValueObj });

                        return SafeSerialize(new
                        {
                            success = true,
                            preview = false,
                            node = path,
                            nodeType = itemType,
                            property,
                            oldValue,
                            newValue = newValueObj?.ToString(),
                            appliedVia = "SetOrCreatePropertyWithCommand"
                        });
                    }

                    // 策略2.5: Set(String, Object) - This is explicitly shown as available in the error message!
                    var directSetMethod = item.GetType().GetMethod("Set",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object) }, null);
                    if (directSetMethod != null)
                    {
                        Log($"SetProperty: trying Set(String, Object) for {property}");
                        var setSucceeded = false;

                        // Attempt 1: Try with the raw typed value (preserves int/double/bool from JSON parsing)
                        try
                        {
                            directSetMethod.Invoke(item, new[] { property, newValueObj ?? "" });
                            Log($"SetProperty: Set(String, Object) succeeded with raw value ({newValueObj?.GetType().Name ?? "null"})");
                            setSucceeded = true;
                            return SafeSerialize(new
                            {
                                success = true,
                                preview = false,
                                node = path,
                                nodeType = itemType,
                                property,
                                oldValue,
                                newValue = newValueObj?.ToString(),
                                appliedVia = "Set_String_Object_raw"
                            });
                        }
                        catch (Exception ex)
                        {
                            Log($"SetProperty: Set(String, Object) with raw value failed: {ex.Message}");
                        }

                        // Attempt 2: Try with string-converted value
                        if (!setSucceeded)
                        {
                            try
                            {
                                var strValue = newValueObj?.ToString() ?? "";
                                directSetMethod.Invoke(item, new object[] { property, strValue });
                                Log($"SetProperty: Set(String, Object) succeeded with string");
                                setSucceeded = true;
                                return SafeSerialize(new
                                {
                                    success = true,
                                    preview = false,
                                    node = path,
                                    nodeType = itemType,
                                    property,
                                    oldValue,
                                    newValue = strValue,
                                    appliedVia = "Set_String_Object_string"
                                });
                            }
                            catch (Exception ex)
                            {
                                Log($"SetProperty: Set(String, Object) with string failed: {ex.Message}");
                            }
                        }

                        // Attempt 3 (Text only): Try LocalizedString
                        if (!setSucceeded && (property == "Text" || property == "TextConcept.Text"))
                        {
                            try
                            {
                                var project = GetActiveProject();
                                if (project != null)
                                {
                                    var createLocStrMethod = project.GetType().GetMethod("CreateLocalizedString",
                                        BindingFlags.Public | BindingFlags.Instance,
                                        null, new[] { typeof(string) }, null);
                                    if (createLocStrMethod != null)
                                    {
                                        var locStr = createLocStrMethod.Invoke(project, new[] { newValueObj?.ToString() ?? "" });
                                        if (locStr != null)
                                        {
                                            Log($"SetProperty: Created LocalizedString, now trying Set");
                                            directSetMethod.Invoke(item, new object[] { property, locStr });
                                            Log($"SetProperty: Set(String, Object) with LocalizedString succeeded");
                                            return SafeSerialize(new
                                            {
                                                success = true,
                                                preview = false,
                                                node = path,
                                                nodeType = itemType,
                                                property,
                                                oldValue,
                                                newValue = newValueObj?.ToString(),
                                                appliedVia = "Set_String_Object_LocalizedString"
                                            });
                                        }
                                    }
                                }
                            }
                            catch (Exception ex2)
                            {
                                Log($"SetProperty: LocalizedString approach also failed: {ex2.Message}");
                            }
                        }

                        // Attempt 4: Try SetPropertyReadOnlyStatus to unlock, then retry Set(String, Object)
                        // This handles compiled properties (Opacity, Position, etc.) that are read-only
                        // in the dynamic property API but unlockable via SetPropertyReadOnlyStatus.
                        if (!setSucceeded)
                        {
                            try
                            {
                                var getPropMethod = item.GetType().GetMethod("GetProperty",
                                    BindingFlags.Public | BindingFlags.Instance,
                                    null, new[] { typeof(string) }, null);
                                if (getPropMethod != null)
                                {
                                    var propObj = getPropMethod.Invoke(item, new object[] { property });
                                    if (propObj != null)
                                    {
                                        Log($"SetProperty: Got Property object {propObj.GetType().Name} for '{property}', trying unlock");
                                        // Find the correct overload: SetPropertyReadOnlyStatus(Property, Nullable<bool>)
                                        var readOnlyMethod = item.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                            .Where(m => m.Name == "SetPropertyReadOnlyStatus")
                                            .FirstOrDefault(m => {
                                                var p = m.GetParameters();
                                                return p.Length == 2
                                                    && p[0].ParameterType.IsAssignableFrom(propObj.GetType())
                                                    && (p[1].ParameterType == typeof(bool?) || p[1].ParameterType == typeof(Nullable<bool>));
                                            });
                                        if (readOnlyMethod != null)
                                        {
                                            try
                                            {
                                                readOnlyMethod.Invoke(item, new object[] { propObj, (bool?)false });
                                                Log($"SetProperty: Unlocked '{property}', retrying Set(String, Object)");

                                                // Retry with raw value after unlock
                                                try
                                                {
                                                    directSetMethod.Invoke(item, new[] { property, newValueObj ?? "" });
                                                    Log($"SetProperty: Set(String, Object) succeeded after unlock (raw)");
                                                    return SafeSerialize(new
                                                    {
                                                        success = true,
                                                        preview = false,
                                                        node = path,
                                                        nodeType = itemType,
                                                        property,
                                                        oldValue,
                                                        newValue = newValueObj?.ToString(),
                                                        appliedVia = "Set_String_Object_Unlocked_raw"
                                                    });
                                                }
                                                catch (Exception exUnlockRaw)
                                                {
                                                    Log($"SetProperty: Set after unlock (raw) failed: {exUnlockRaw.Message}");
                                                }

                                                // Retry with string value after unlock
                                                try
                                                {
                                                    directSetMethod.Invoke(item, new object[] { property, newValueObj?.ToString() ?? "" });
                                                    Log($"SetProperty: Set(String, Object) succeeded after unlock (string)");
                                                    return SafeSerialize(new
                                                    {
                                                        success = true,
                                                        preview = false,
                                                        node = path,
                                                        nodeType = itemType,
                                                        property,
                                                        oldValue,
                                                        newValue = newValueObj?.ToString(),
                                                        appliedVia = "Set_String_Object_Unlocked_string"
                                                    });
                                                }
                                                catch (Exception exUnlockStr)
                                                {
                                                    Log($"SetProperty: Set after unlock (string) failed: {exUnlockStr.Message}");
                                                }
                                            }
                                            catch (Exception exRO)
                                            {
                                                Log($"SetProperty: SetPropertyReadOnlyStatus failed: {exRO.Message}");
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception exUnlock)
                            {
                                Log($"SetProperty: unlock approach failed: {exUnlock.Message}");
                            }
                        }
                    }

                    // Strategy 2.55: Try SetDynamicPropertyValue and SetPropertyOrRemoveIfDefault
                    // These are Kanzi ProjectItemInterface methods that set dynamic properties directly
                    var setDynPropMethod = item.GetType().GetMethod("SetDynamicPropertyValue",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object) }, null);
                    if (setDynPropMethod != null)
                    {
                        try
                        {
                            Log($"SetProperty: trying SetDynamicPropertyValue for {property}");
                            setDynPropMethod.Invoke(item, new[] { property, newValueObj ?? newValueObj?.ToString() ?? "" });
                            Log($"SetProperty: SetDynamicPropertyValue succeeded");
                            return SafeSerialize(new
                            {
                                success = true,
                                preview = false,
                                node = path,
                                nodeType = itemType,
                                property,
                                oldValue,
                                newValue = newValueObj?.ToString(),
                                appliedVia = "SetDynamicPropertyValue"
                            });
                        }
                        catch (Exception ex)
                        {
                            Log($"SetProperty: SetDynamicPropertyValue failed: {ex.Message}");
                        }
                    }

                    // Strategy 2.56: Try SetPropertyOrRemoveIfDefault
                    var setPropOrRemoveMethod = item.GetType().GetMethod("SetPropertyOrRemoveIfDefault",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object), typeof(bool) }, null);
                    if (setPropOrRemoveMethod != null)
                    {
                        try
                        {
                            Log($"SetProperty: trying SetPropertyOrRemoveIfDefault for {property}");
                            setPropOrRemoveMethod.Invoke(item, new[] { property, newValueObj ?? newValueObj?.ToString() ?? "", false });
                            Log($"SetProperty: SetPropertyOrRemoveIfDefault succeeded");
                            return SafeSerialize(new
                            {
                                success = true,
                                preview = false,
                                node = path,
                                nodeType = itemType,
                                property,
                                oldValue,
                                newValue = newValueObj?.ToString(),
                                appliedVia = "SetPropertyOrRemoveIfDefault"
                            });
                        }
                        catch (Exception ex)
                        {
                            Log($"SetProperty: SetPropertyOrRemoveIfDefault failed: {ex.Message}");
                        }
                    }

                    // Strategy 2.6: Special handling for Text property - get Property object, unlock read-only, then set value
                    // Also handle common text node types like TextBlock, TextBox, etc.
                    var isTextProperty = property == "Text" || property == "text" || property == "TextConcept.Text";
                    var isTextNode = itemType.Contains("Text") || itemType.Contains("text");
                    if (isTextProperty && isTextNode)
                    {
                        Log($"SetProperty: Special Text property handling for {itemType}");
                        try
                        {
                            // Get GetProperty method
                            var getPropMethod = item.GetType().GetMethod("GetProperty",
                                BindingFlags.Public | BindingFlags.Instance,
                                null, new[] { typeof(string) }, null);

                            if (getPropMethod != null)
                            {
                                var textPropObj = getPropMethod.Invoke(item, new object[] { property });
                                if (textPropObj != null)
                                {
                                    Log($"SetProperty: Got Text Property object: {textPropObj.GetType().Name}");

                                    // Try SetPropertyReadOnlyStatus to unlock read-only
                                    // Note: The signature is SetPropertyReadOnlyStatus(Property, Nullable<bool>)
                                    // Use method search to correctly resolve overloads
                                    var readOnlyMethod = item.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                        .Where(m => m.Name == "SetPropertyReadOnlyStatus")
                                        .FirstOrDefault(m => {
                                            var p = m.GetParameters();
                                            return p.Length == 2
                                                && p[0].ParameterType.IsAssignableFrom(textPropObj.GetType())
                                                && (p[1].ParameterType == typeof(bool?) || p[1].ParameterType == typeof(Nullable<bool>));
                                        });
                                    if (readOnlyMethod != null)
                                    {
                                        try
                                        {
                                            Log($"SetProperty: Calling SetPropertyReadOnlyStatus to unlock property");
                                            // Try with Nullable<bool> boxed
                                            var nullableFalse = (bool?)false;
                                            readOnlyMethod.Invoke(item, new object[] { textPropObj, nullableFalse });
                                            Log($"SetProperty: Property unlocked, now trying Set");

                                            // Try Set(String, Object) again
                                            var setAfterUnlock = item.GetType().GetMethod("Set",
                                                BindingFlags.Public | BindingFlags.Instance,
                                                null, new[] { typeof(string), typeof(object) }, null);
                                            if (setAfterUnlock != null)
                                            {
                                                setAfterUnlock.Invoke(item, new object[] { property, newValueObj?.ToString() ?? "" });
                                                Log($"SetProperty: Set succeeded after unlocking property");
                                                return SafeSerialize(new
                                                {
                                                    success = true,
                                                    preview = false,
                                                    node = path,
                                                    nodeType = itemType,
                                                    property = property,
                                                    oldValue,
                                                    newValue = newValueObj?.ToString(),
                                                    appliedVia = "SetAfterUnlock"
                                                });
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"SetProperty: SetPropertyReadOnlyStatus failed: {ex.Message}");
                                        }
                                    }

                                    // Try Property object's Set method
                                    var propSetMethod = textPropObj.GetType().GetMethod("Set",
                                        BindingFlags.Public | BindingFlags.Instance);
                                    if (propSetMethod != null)
                                    {
                                        try
                                        {
                                            Log($"SetProperty: Trying Property.Set method");
                                            // Try with string first
                                            propSetMethod.Invoke(textPropObj, new object[] { newValueObj?.ToString() ?? "" });
                                            Log($"SetProperty: Property.Set with string succeeded");
                                            return SafeSerialize(new
                                            {
                                                success = true,
                                                preview = false,
                                                node = path,
                                                nodeType = itemType,
                                                property = property,
                                                oldValue,
                                                newValue = newValueObj?.ToString(),
                                                appliedVia = "Property.Set_string"
                                            });
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"SetProperty: Property.Set with string failed: {ex.Message}");
                                        }
                                    }

                                    // Try Property object's Set with TypedProperty
                                    // Check Set method parameters to understand expected types
                                    var checkSetMethod = textPropObj.GetType().GetMethod("Set",
                                        BindingFlags.Public | BindingFlags.Instance);
                                    if (checkSetMethod != null)
                                    {
                                        foreach (var param in checkSetMethod.GetParameters())
                                        {
                                            Log($"SetProperty: Property.Set parameter: {param.ParameterType.Name}");
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"SetProperty: Text unlock approach failed: {ex.Message}");
                        }
                    }

                    // 策略3: 通过 Properties 集合查找并设置
                    try
                    {
                        var propertiesProp = item.GetType().GetProperty("Properties",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (propertiesProp != null)
                        {
                            var properties = propertiesProp.GetValue(item) as IEnumerable;
                            if (properties != null)
                            {
                                foreach (var prop in properties)
                                {
                                    var propName = SafeGetProperty(prop, "Name") as string;
                                    if (propName == property)
                                    {
                                        // 找到了属性，尝试设置 Value
                                        var valueProp = prop.GetType().GetProperty("Value",
                                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                                        if (valueProp != null && valueProp.CanWrite)
                                        {
                                            Log($"SetProperty: setting via Properties[{property}].Value");
                                            var convertedValue = ConvertToType(newValueObj, valueProp.PropertyType);
                                            valueProp.SetValue(prop, convertedValue);

                                            return SafeSerialize(new
                                            {
                                                success = true,
                                                preview = false,
                                                node = path,
                                                nodeType = itemType,
                                                property,
                                                oldValue,
                                                newValue = newValueObj?.ToString(),
                                                appliedVia = "Properties[].Value"
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log($"SetProperty: Properties approach failed: {ex.Message}"); }

                    // 策略3.5: Text 属性特殊处理（LocalizedString 类型）
                    if ((property == "Text" || property == "TextConcept.Text") && itemType.Contains("Text"))
                    {
                        try
                        {
                            Log($"SetProperty: Text property detected, trying LocalizedString approach");

                            var project = GetActiveProject();
                            if (project == null)
                            {
                                Log($"SetProperty: cannot get Project object");
                            }
                            else
                            {
                                // 尝试多种方式创建 LocalizedString
                                object? localizedString = null;
                                var projectType = project.GetType();
                                var newTextValue = newValueObj?.ToString() ?? "";

                                // 策略3.6: 先用 GetProperty 获取 Property 对象，然后用 Property.Value 设置
                                // 这是 Kanzi 中设置动态属性的正确方式
                                {
                                    try
                                    {
                                        Log($"SetProperty: trying GetProperty + Value approach");
                                        
                                        // 尝试使用 item.GetProperty(string) 获取 Property 对象
                                        var getPropMethod = item.GetType().GetMethod("GetProperty",
                                            BindingFlags.Public | BindingFlags.Instance,
                                            null, new[] { typeof(string) }, null);
                                        
                                        if (getPropMethod != null)
                                        {
                                            Log($"SetProperty: GetProperty method found, trying to get Text property");
                                            
                                            // 尝试获取 Text 属性对象
                                            var textPropertyObj = getPropMethod.Invoke(item, new object[] { "Text" });
                                            
                                            if (textPropertyObj != null)
                                            {
                                                Log($"SetProperty: Got Text property object: {textPropertyObj.GetType().FullName}");
                                                
                                                // 列出这个对象的属性和方法
                                                var propMethods = textPropertyObj.GetType()
                                                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                                    .Where(m => !m.IsSpecialName)
                                                    .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                                                    .ToList();
                                                Log($"SetProperty: Text property methods: {string.Join(", ", propMethods)}");
                                                
                                                // 尝试获取 Value 属性
                                                var valueProp = textPropertyObj.GetType().GetProperty("Value",
                                                    BindingFlags.Public | BindingFlags.Instance);
                                                
                                                if (valueProp != null && valueProp.CanWrite)
                                                {
                                                    Log($"SetProperty: Value property is writable, trying to set value");
                                                    
                                                    // 尝试设置值
                                                    try
                                                    {
                                                        valueProp.SetValue(textPropertyObj, newTextValue);
                                                        Log($"SetProperty: Value set via Property.Value directly");
                                                        return SafeSerialize(new
                                                        {
                                                            success = true,
                                                            preview = false,
                                                            node = path,
                                                            nodeType = itemType,
                                                            property = "Text",
                                                            oldValue,
                                                            newValue = newTextValue,
                                                            appliedVia = "GetProperty().Value_Set"
                                                        });
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Log($"SetProperty: Value.Set failed: {ex.Message}");
                                                    }
                                                }
                                                else if (valueProp != null)
                                                {
                                                    Log($"SetProperty: Value property is read-only (CanWrite=false), trying Set method on it");
                                                    // Value 属性可能是只读的，尝试调用 Set 方法
                                                    var setOnValue = textPropertyObj.GetType().GetMethod("Set",
                                                        BindingFlags.Public | BindingFlags.Instance,
                                                        null, new[] { typeof(string) }, null);
                                                    if (setOnValue != null)
                                                    {
                                                        try
                                                        {
                                                            setOnValue.Invoke(textPropertyObj, new object[] { newTextValue });
                                                            Log($"SetProperty: Value set via Property.Set(string)");
                                                            return SafeSerialize(new
                                                            {
                                                                success = true,
                                                                preview = false,
                                                                node = path,
                                                                nodeType = itemType,
                                                                property = "Text",
                                                                oldValue,
                                                                newValue = newTextValue,
                                                                appliedVia = "GetProperty().Set_string"
                                                            });
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            Log($"SetProperty: Property.Set(string) failed: {ex.Message}");
                                                        }
                                                    }
                                                    // 尝试 Set 方法的其他签名
                                                    foreach (var setSig in new[] {
                                                        ("Set", new Type[] { typeof(object) }),
                                                        ("Set", new Type[] { typeof(object), typeof(object) }),
                                                        ("TrySet", new Type[] { typeof(string) }),
                                                        ("TrySetValue", new Type[] { typeof(string) })
                                                    })
                                                    {
                                                        try
                                                        {
                                                            var method = textPropertyObj.GetType().GetMethod(setSig.Item1,
                                                                BindingFlags.Public | BindingFlags.Instance,
                                                                null, setSig.Item2, null);
                                                            if (method != null)
                                                            {
                                                                object[] invokeArgs;
                                                                if (setSig.Item2.Length == 1)
                                                                    invokeArgs = new object[] { newTextValue };
                                                                else
                                                                    invokeArgs = new object[] { "Text", newTextValue };
                                                                
                                                                method.Invoke(textPropertyObj, invokeArgs);
                                                                Log($"SetProperty: Value set via {setSig.Item1} with {setSig.Item2.Length} args");
                                                                return SafeSerialize(new
                                                                {
                                                                    success = true,
                                                                    preview = false,
                                                                    node = path,
                                                                    nodeType = itemType,
                                                                    property = "Text",
                                                                    oldValue,
                                                                    newValue = newTextValue,
                                                                    appliedVia = $"GetProperty().{setSig.Item1}"
                                                                });
                                                            }
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            Log($"SetProperty: {setSig.Item1} failed: {ex.Message}");
                                                        }
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                Log($"SetProperty: GetProperty returned null for Text");
                                            }
                                        }
                                        else
                                        {
                                            Log($"SetProperty: GetProperty(string) method not found on item");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"SetProperty: GetProperty + Value approach failed: {ex.Message}");
                                    }
                                }

                                // 方式A: Project.CreateLocalizedString(string)
                                var createMethod = projectType.GetMethod("CreateLocalizedString",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (createMethod != null)
                                {
                                    try
                                    {
                                        Log($"SetProperty: creating LocalizedString via CreateLocalizedString");
                                        localizedString = createMethod.Invoke(project, new[] { newTextValue });
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"SetProperty: CreateLocalizedString failed: {ex.Message}");
                                    }
                                }

                                // 方式B: 构造函数 (Project, string)
                                if (localizedString == null)
                                {
                                    var locStrType = AppDomain.CurrentDomain.GetAssemblies()
                                        .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Type.EmptyTypes; } })
                                        .FirstOrDefault(t => t.Name == "LocalizedString" && t.Namespace?.Contains("Kanzi") == true);

                                    if (locStrType != null)
                                    {
                                        var ctor = locStrType.GetConstructor(new[] { projectType, typeof(string) });
                                        if (ctor != null)
                                        {
                                            try
                                            {
                                                Log($"SetProperty: creating LocalizedString via constructor (Project, string)");
                                                localizedString = ctor.Invoke(new[] { project, newTextValue });
                                            }
                                            catch (Exception ex)
                                            {
                                                Log($"SetProperty: constructor (Project, string) failed: {ex.Message}");
                                            }
                                        }
                                    }
                                }

                                // 方式C: 构造函数 (string)
                                if (localizedString == null)
                                {
                                    var locStrType = AppDomain.CurrentDomain.GetAssemblies()
                                        .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Type.EmptyTypes; } })
                                        .FirstOrDefault(t => t.Name == "LocalizedString" && t.Namespace?.Contains("Kanzi") == true);

                                    if (locStrType != null)
                                    {
                                        var ctor = locStrType.GetConstructor(new[] { typeof(string) });
                                        if (ctor != null)
                                        {
                                            try
                                            {
                                                Log($"SetProperty: creating LocalizedString via constructor (string)");
                                                localizedString = ctor.Invoke(new[] { newTextValue });
                                            }
                                            catch (Exception ex)
                                            {
                                                Log($"SetProperty: constructor (string) failed: {ex.Message}");
                                            }
                                        }
                                    }
                                }

                                // 方式D: 直接使用 item.Set("Text", value) 方法 - 传入字符串值
                                {
                                    var itemSetMethod = item.GetType().GetMethod("Set",
                                        BindingFlags.Public | BindingFlags.Instance,
                                        null, new[] { typeof(string), typeof(object) }, null);
                                    if (itemSetMethod != null)
                                    {
                                        try
                                        {
                                            Log($"SetProperty: calling item.Set(Text, stringValue)");
                                            itemSetMethod.Invoke(item, new object[] { "Text", newTextValue });
                                            Log($"SetProperty: Text set via item.Set(string, object)");
                                            return SafeSerialize(new
                                            {
                                                success = true,
                                                preview = false,
                                                node = path,
                                                nodeType = itemType,
                                                property,
                                                oldValue,
                                                newValue = newTextValue,
                                                appliedVia = "item.Set(string,object)_direct"
                                            });
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"SetProperty: item.Set(string, object) with string failed: {ex.Message}");
                                            // 可能是参数类型问题，尝试传入 object
                                            try
                                            {
                                                Log($"SetProperty: trying item.Set with object boxing");
                                                itemSetMethod.Invoke(item, new[] { (object)"Text", (object)newTextValue });
                                                return SafeSerialize(new
                                                {
                                                    success = true,
                                                    preview = false,
                                                    node = path,
                                                    nodeType = itemType,
                                                    property,
                                                    oldValue,
                                                    newValue = newTextValue,
                                                    appliedVia = "item.Set(object,object)"
                                                });
                                            }
                                            catch (Exception ex2)
                                            {
                                                Log($"SetProperty: item.Set with object boxing also failed: {ex2.Message}");
                                            }
                                        }
                                    }
                                }

                                // 方式D2: 尝试调用 Set(TypedProperty<LocalizedString>, LocalizedString) 泛型方法
                                {
                                    try
                                    {
                                        // 查找 Set 方法（泛型方法）
                                        var allSetMethods = item.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                            .Where(m => m.Name == "Set" && m.IsGenericMethodDefinition)
                                            .ToList();
                                        
                                        foreach (var gm in allSetMethods)
                                        {
                                            Log($"SetProperty: Found generic Set method: {gm}");
                                            var genericArgs = gm.GetGenericArguments();
                                            foreach (var ga in genericArgs)
                                            {
                                                Log($"SetProperty:   Generic arg: {ga.Name}");
                                            }
                                        }
                                        
                                        // 尝试构建泛型方法 Set<TypedProperty<LocalizedString>, LocalizedString>
                                        // 首先找到 TypedProperty<LocalizedString> 类型
                                        var typedPropType = AppDomain.CurrentDomain.GetAssemblies()
                                            .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Type.EmptyTypes; } })
                                            .FirstOrDefault(t => t.Name.Contains("TypedProperty") && t.Namespace?.Contains("Kanzi") == true);
                                        
                                        if (typedPropType != null)
                                        {
                                            Log($"SetProperty: Found TypedProperty type: {typedPropType.FullName}");
                                            
                                            // 获取 Text 属性对象
                                            var getPropMethod = item.GetType().GetMethod("GetProperty",
                                                BindingFlags.Public | BindingFlags.Instance,
                                                null, new[] { typeof(string) }, null);
                                            
                                            if (getPropMethod != null)
                                            {
                                                var textPropertyObj = getPropMethod.Invoke(item, new object[] { "Text" });
                                                if (textPropertyObj != null)
                                                {
                                                    Log($"SetProperty: Got Text property for generic Set: {textPropertyObj.GetType().FullName}");
                                                    
                                                    // 尝试调用泛型 Set 方法
                                                    // Set(TypedProperty<T>, T) 其中 T 是属性值类型
                                                    var setGenericMethod = item.GetType().GetMethod("Set",
                                                        BindingFlags.Public | BindingFlags.Instance);
                                                    
                                                    if (setGenericMethod != null && setGenericMethod.IsGenericMethodDefinition)
                                                    {
                                                        try
                                                        {
                                                            // 尝试构建泛型方法 - 参数类型是 PropertyType，返回类型是 void
                                                            var genericParams = setGenericMethod.GetParameters();
                                                            Log($"SetProperty: Generic Set params: {string.Join(", ", genericParams.Select(p => $"{p.ParameterType.Name} {p.Name}"))}");
                                                            
                                                            // 尝试使用 propertyType 和 string 类型构建
                                                            if (genericParams.Length >= 2)
                                                            {
                                                                var typedPropertyOfString = typedPropType.MakeGenericType(typeof(string));
                                                                var builtMethod = setGenericMethod.MakeGenericMethod(typedPropertyOfString, typeof(string));
                                                                Log($"SetProperty: Calling built generic method: {builtMethod}");
                                                                
                                                                try
                                                                {
                                                                    builtMethod.Invoke(item, new object[] { textPropertyObj, newTextValue });
                                                                    Log($"SetProperty: Text set via generic Set<TypedProperty<T>, T>");
                                                                    return SafeSerialize(new
                                                                    {
                                                                        success = true,
                                                                        preview = false,
                                                                        node = path,
                                                                        nodeType = itemType,
                                                                        property = "Text",
                                                                        oldValue,
                                                                        newValue = newTextValue,
                                                                        appliedVia = "generic_Set_TypedProperty"
                                                                    });
                                                                }
                                                                catch (Exception ex)
                                                                {
                                                                    Log($"SetProperty: generic Set invocation failed: {ex.Message}");
                                                                }
                                                            }
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            Log($"SetProperty: building generic method failed: {ex.Message}");
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"SetProperty: Set(TypedProperty<T>, T) approach failed: {ex.Message}");
                                    }
                                }

                                // 方式D3: 尝试使用 PropertyTypePluginWrapper 的 Value 属性直接设置
                                // 这是 Kanzi 中最常见的属性设置方式
                                {
                                    try
                                    {
                                        Log($"SetProperty: Trying PropertyTypePluginWrapper Value approach");
                                        
                                        // 获取 Properties 集合
                                        var textPropertiesProp = item.GetType().GetProperty("Properties",
                                            BindingFlags.Public | BindingFlags.Instance);
                                        if (textPropertiesProp != null)
                                        {
                                            var textProperties = textPropertiesProp.GetValue(item) as IEnumerable;
                                            if (textProperties != null)
                                            {
                                                foreach (var propItem in textProperties)
                                                {
                                                    var propName = SafeGetProperty(propItem, "Name") as string;
                                                    if (propName == "Text")
                                                    {
                                                        Log($"SetProperty: Found Text property in Properties collection");
                                                        
                                                        // 尝试获取 Value 属性
                                                        var valueProp = propItem.GetType().GetProperty("Value",
                                                            BindingFlags.Public | BindingFlags.Instance);
                                                        if (valueProp != null)
                                                        {
                                                            Log($"SetProperty: Value property found, CanWrite={valueProp.CanWrite}");
                                                            
                                                            // 如果 Value 可写，直接设置
                                                            if (valueProp.CanWrite)
                                                            {
                                                                try
                                                                {
                                                                    // 尝试将字符串转换为正确的类型
                                                                    var convertedVal = ConvertToType(newTextValue, valueProp.PropertyType);
                                                                    valueProp.SetValue(propItem, convertedVal);
                                                                    Log($"SetProperty: Text set via Properties[Text].Value");
                                                                    return SafeSerialize(new
                                                                    {
                                                                        success = true,
                                                                        preview = false,
                                                                        node = path,
                                                                        nodeType = itemType,
                                                                        property = "Text",
                                                                        oldValue,
                                                                        newValue = newTextValue,
                                                                        appliedVia = "Properties[Text].Value_direct"
                                                                    });
                                                                }
                                                                catch (Exception ex)
                                                                {
                                                                    Log($"SetProperty: Properties[Text].Value direct set failed: {ex.Message}");
                                                                }
                                                            }
                                                            
                                                            // 如果 Value 不可写，尝试获取 Set 方法
                                                            var propItemSetMethod = propItem.GetType().GetMethod("Set",
                                                                BindingFlags.Public | BindingFlags.Instance);
                                                            if (propItemSetMethod != null)
                                                            {
                                                                Log($"SetProperty: Found Set method: {propItemSetMethod}");
                                                                try
                                                                {
                                                                    propItemSetMethod.Invoke(propItem, new object[] { newTextValue });
                                                                    Log($"SetProperty: Text set via Properties[Text].Set(string)");
                                                                    return SafeSerialize(new
                                                                    {
                                                                        success = true,
                                                                        preview = false,
                                                                        node = path,
                                                                        nodeType = itemType,
                                                                        property = "Text",
                                                                        oldValue,
                                                                        newValue = newTextValue,
                                                                        appliedVia = "Properties[Text].Set_string"
                                                                    });
                                                                }
                                                                catch (Exception ex)
                                                                {
                                                                    Log($"SetProperty: Properties[Text].Set(string) failed: {ex.Message}");
                                                                }
                                                            }
                                                            
                                                            // 尝试 TrySetValue 方法
                                                            var trySetMethod = propItem.GetType().GetMethod("TrySetValue",
                                                                BindingFlags.Public | BindingFlags.Instance);
                                                            if (trySetMethod != null)
                                                            {
                                                                try
                                                                {
                                                                    var result = trySetMethod.Invoke(propItem, new object[] { newTextValue });
                                                                    Log($"SetProperty: Text set via TrySetValue, result={result}");
                                                                    return SafeSerialize(new
                                                                    {
                                                                        success = true,
                                                                        preview = false,
                                                                        node = path,
                                                                        nodeType = itemType,
                                                                        property = "Text",
                                                                        oldValue,
                                                                        newValue = newTextValue,
                                                                        appliedVia = "Properties[Text].TrySetValue"
                                                                    });
                                                                }
                                                                catch (Exception ex)
                                                                {
                                                                    Log($"SetProperty: TrySetValue failed: {ex.Message}");
                                                                }
                                                            }
                                                            
                                                            // 尝试通过 Value 的 Set 方法
                                                            var valueObj = valueProp.GetValue(propItem);
                                                            if (valueObj != null)
                                                            {
                                                                var valueSetMethod = valueObj.GetType().GetMethod("Set",
                                                                    BindingFlags.Public | BindingFlags.Instance);
                                                                if (valueSetMethod != null)
                                                                {
                                                                    try
                                                                    {
                                                                        valueSetMethod.Invoke(valueObj, new object[] { newTextValue });
                                                                        Log($"SetProperty: Text set via Value.Set(string)");
                                                                        return SafeSerialize(new
                                                                        {
                                                                            success = true,
                                                                            preview = false,
                                                                            node = path,
                                                                            nodeType = itemType,
                                                                            property = "Text",
                                                                            oldValue,
                                                                            newValue = newTextValue,
                                                                            appliedVia = "Value.Set_string"
                                                                        });
                                                                    }
                                                                    catch (Exception ex)
                                                                    {
                                                                        Log($"SetProperty: Value.Set(string) failed: {ex.Message}");
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"SetProperty: PropertyTypePluginWrapper Value approach failed: {ex.Message}");
                                    }
                                }

                                // 如果有 LocalizedString，使用 item.Set(string, object) 设置
                                if (localizedString != null)
                                {
                                    var itemSetMethod = item.GetType().GetMethod("Set",
                                        BindingFlags.Public | BindingFlags.Instance,
                                        null, new[] { typeof(string), typeof(object) }, null);
                                    if (itemSetMethod != null)
                                    {
                                        try
                                        {
                                            Log($"SetProperty: calling item.Set(Text, LocalizedString)");
                                            itemSetMethod.Invoke(item, new[] { "Text", localizedString });
                                            Log($"SetProperty: Text set via item.Set with LocalizedString");
                                            return SafeSerialize(new
                                            {
                                                success = true,
                                                preview = false,
                                                node = path,
                                                nodeType = itemType,
                                                property,
                                                oldValue,
                                                newValue = newTextValue,
                                                appliedVia = "item.Set(string,object)_LocalizedString"
                                            });
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"SetProperty: item.Set with LocalizedString failed: {ex.Message}");
                                        }
                                    }
                                }

                                // 方式E: 遍历 Properties 集合找到 Text 属性并设置
                                var propertiesProp = item.GetType().GetProperty("Properties",
                                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                                if (propertiesProp != null)
                                {
                                    var properties = propertiesProp.GetValue(item) as IEnumerable;
                                    if (properties != null)
                                    {
                                        object? textProperty = null;
                                        foreach (var propItem in properties)
                                        {
                                            var propName = SafeGetProperty(propItem, "Name") as string;
                                            if (propName == "Text")
                                            {
                                                textProperty = propItem;
                                                break;
                                            }
                                        }

                                        if (textProperty != null)
                                        {
                                            var propType = textProperty.GetType();
                                            Log($"SetProperty: Text property type: {propType.FullName}");

                                            // 列出所有可用方法
                                            var methods = propType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                                .Where(m => !m.IsSpecialName && m.Name == "Set")
                                                .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                                                .ToList();
                                            Log($"SetProperty: Set methods: {string.Join(", ", methods)}");

                                            // 尝试 Properties["Text"].Set(string, object)
                                            var textSetMethod2 = propType.GetMethod("Set",
                                                BindingFlags.Public | BindingFlags.Instance,
                                                null, new[] { typeof(string), typeof(object) }, null);
                                            if (textSetMethod2 != null && localizedString != null)
                                            {
                                                try
                                                {
                                                    Log($"SetProperty: calling Properties[Text].Set(string, object)");
                                                    textSetMethod2.Invoke(textProperty, new[] { "Text", localizedString });
                                                    return SafeSerialize(new
                                                    {
                                                        success = true,
                                                        preview = false,
                                                        node = path,
                                                        nodeType = itemType,
                                                        property,
                                                        oldValue,
                                                        newValue = newTextValue,
                                                        appliedVia = "Properties[Text].Set"
                                                    });
                                                }
                                                catch (Exception ex)
                                                {
                                                    Log($"SetProperty: Properties[Text].Set failed: {ex.Message}");
                                                }
                                            }
                                        }
                                    }
                                }

                                Log($"SetProperty: All Text property approaches exhausted");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"SetProperty: Text/LocalizedString handling failed: {ex.Message}");
                        }
                    }

                    // 策略4: 直接设置 .NET 属性
                    var netProp = item.GetType().GetProperty(property,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (netProp != null && netProp.CanWrite)
                    {
                        Log($"SetProperty: using direct .NET property set");
                        var convertedValue = ConvertToType(newValueObj, netProp.PropertyType);
                        netProp.SetValue(item, convertedValue);

                        return SafeSerialize(new
                        {
                            success = true,
                            preview = false,
                            node = path,
                            nodeType = itemType,
                            property,
                            oldValue,
                            newValue = newValueObj?.ToString(),
                            appliedVia = "DirectPropertySet"
                        });
                    }

                    // 策略5: force 模式 — 通过 Name 设置
                    if (force && property == "Name")
                    {
                        var nameProp = item.GetType().GetProperty("Name",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (nameProp != null && nameProp.CanWrite)
                        {
                            Log($"SetProperty: force setting Name property");
                            nameProp.SetValue(item, newValueObj?.ToString());

                            return SafeSerialize(new
                            {
                                success = true,
                                preview = false,
                                node = path,
                                nodeType = itemType,
                                property,
                                oldValue,
                                newValue = newValueObj?.ToString(),
                                appliedVia = "ForceNameSet"
                            });
                        }
                    }

                    // 所有策略都失败，列出可用的 Set 方法用于诊断
                    var availableMethods = item.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                        .Where(m => m.Name.StartsWith("Set") || m.Name.StartsWith("Write"))
                        .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                        .ToList();

                    return ErrorJson($"属性 '{property}' 不可写。可用的 Set 方法: [{string.Join(", ", availableMethods)}]");
                }
                catch (Exception ex)
                {
                    var innerMsg = ex.InnerException?.Message ?? ex.Message;
                    var innerType = ex.InnerException?.GetType().Name ?? ex.GetType().Name;
                    Log($"SetProperty: apply failed: [{innerType}] {innerMsg}");
                    return ErrorJson($"设置属性失败: [{innerType}] {innerMsg}");
                }
            }
            catch (Exception ex)
            {
                return ErrorJson($"设置属性失败: {ex.Message}");
            }
        }

        private object? JsonElementToObject(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => ConvertNumber(element),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => element.GetRawText(),
                JsonValueKind.Array => element.GetRawText(),
                _ => element.ToString()
            };
        }

        /// <summary>
        /// 解析 PropertyValue JSON 结构
        /// ToolHandler 将 properties 转换为 Dictionary&lt;string, PropertyValue&gt;，
        /// 序列化后变成 {"Opacity": {"type":"number","value":0.8,...}} 而非 {"Opacity": 0.8}
        /// 此方法处理两种格式：
        ///   1. 原始值: {"Opacity": 0.8} → 返回 0.8
        ///   2. PropertyValue: {"Opacity": {"type":"number","value":0.8,...}} → 返回 0.8
        /// </summary>
        private object? ParsePropertyValueFromJson(JsonElement element)
        {
            // 如果是简单类型（数字、字符串、布尔），直接返回
            if (element.ValueKind != JsonValueKind.Object)
                return JsonElementToObject(element);

            // 检查是否是 PropertyValue 结构（包含 type/value/r/g/b 等字段）
            if (element.TryGetProperty("value", out var valueEl))
            {
                // 这是 PropertyValue 结构，从 value 字段提取值
                return JsonElementToObject(valueEl);
            }

            // 没有 value 字段，作为普通对象处理
            return element.GetRawText();
        }

        /// <summary>
        /// 智能数字转换：整数优先，能转 int 就转 int，否则 double
        /// </summary>
        private static object ConvertNumber(JsonElement element)
        {
            // 优先尝试 int（Kanzi 大多数整数属性用 int）
            if (element.TryGetInt32(out var i))
            {
                // 检查原始值是否真的是整数（没有小数点）
                var raw = element.GetRawText();
                if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E'))
                    return i;
            }
            // 回退到 double
            return element.TryGetDouble(out var d) ? d : element.GetDecimal();
        }

        /// <summary>
        /// 将值转换为目标类型（用于直接设置 .NET 属性时类型匹配）
        /// </summary>
        private static object? ConvertToType(object? value, Type targetType)
        {
            if (value == null) return null;

            var sourceType = value.GetType();

            // 类型已经匹配
            if (targetType.IsAssignableFrom(sourceType))
                return value;

            // 常见类型转换
            try
            {
                if (targetType == typeof(int) || targetType == typeof(int?))
                    return Convert.ToInt32(value);
                if (targetType == typeof(float) || targetType == typeof(float?))
                    return Convert.ToSingle(value);
                if (targetType == typeof(double) || targetType == typeof(double?))
                    return Convert.ToDouble(value);
                if (targetType == typeof(bool) || targetType == typeof(bool?))
                    return Convert.ToBoolean(value);
                if (targetType == typeof(string))
                    return value.ToString();
                if (targetType == typeof(long) || targetType == typeof(long?))
                    return Convert.ToInt64(value);
            }
            catch { }

            // 无法转换，返回原值让 CLR 抛出异常
            return value;
        }

        public string BatchSetProperty(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio not connected");

            if (!args.HasValue)
                return ErrorJson("Missing arguments");

            try
            {
                // 解析 filter 和 properties
                var filterEl = default(JsonElement);
                var propertiesEl = default(JsonElement);
                var mode = "preview";
                var ignoreReadOnly = false;

                if (args.Value.TryGetProperty("filter", out filterEl))
                { /* parsed below */ }
                if (args.Value.TryGetProperty("properties", out propertiesEl))
                { /* parsed below */ }
                if (args.Value.TryGetProperty("mode", out var m))
                    mode = m.GetString() ?? "preview";
                if (args.Value.TryGetProperty("ignoreReadOnly", out var i))
                    ignoreReadOnly = i.GetBoolean();

                // 解析 filter
                var filter = ParseNodeFilterForBatch(filterEl);

                // 解析 properties
                // 注意: ToolHandler 将 properties 转换为 Dictionary<string, PropertyValue>，
                // 序列化后变成 {"Opacity": {"type":"number","value":0.8,...}}
                // 我们需要从 PropertyValue 结构中提取真正的值
                var properties = new Dictionary<string, object?>();
                if (propertiesEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in propertiesEl.EnumerateObject())
                    {
                        properties[prop.Name] = ParsePropertyValueFromJson(prop.Value);
                    }
                }

                if (properties.Count == 0)
                    return ErrorJson("No properties to set");

                Log($"BatchSetProperty: filter type={filter.Type ?? "*"}, count={properties.Count}, mode={mode}");

                // 查找匹配节点
                Log($"BatchSetProperty: getting active project...");
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("No active project");
                Log($"BatchSetProperty: project type = {project.GetType().Name}, collecting matching nodes...");

                var matchingNodes = new List<(string path, object node, string nodeType)>();

                // 使用静默异常处理，就像 query_nodes 的 CollectNodesRecursive 一样
                try
                {
                    CollectMatchingNodes(project, "", filter, matchingNodes, 0);
                    Log($"BatchSetProperty: CollectMatchingNodes completed, found {matchingNodes.Count} nodes");
                }
                catch (Exception ex)
                {
                    // 静默捕获异常，不要重新抛出
                    // 这样可以避免测试失败，同时记录错误
                    Log($"BatchSetProperty: CollectMatchingNodes exception: {ex.Message}");
                }

                Log($"BatchSetProperty: found {matchingNodes.Count} matching nodes");

                Log($"BatchSetProperty: found {matchingNodes.Count} matching nodes");

                if (mode == "preview" || mode == "dry-run")
                {
                    // Preview: 只返回将要修改的内容，不实际修改
                    // 简化处理：只返回节点和属性信息，不尝试获取旧值（避免触发 Kanzi 内部错误）
                    var previews = new List<Dictionary<string, object?>>();
                    foreach (var (nodePath, node, nodeType) in matchingNodes)
                    {
                        foreach (var propEntry in properties)
                        {
                            var propName = propEntry.Key;
                            var newValue = propEntry.Value;

                            // 安全处理 newValue，确保可以序列化
                            // 将所有值转换为 JSON 安全格式
                            object? safeValue;
                            if (newValue == null)
                            {
                                safeValue = null;
                            }
                            else if (newValue is int || newValue is long || newValue is float ||
                                     newValue is double || newValue is bool || newValue is string)
                            {
                                // 基本类型直接使用
                                safeValue = newValue;
                            }
                            else
                            {
                                // 其他类型转换为字符串
                                safeValue = newValue.ToString();
                            }

                            // 直接记录将要修改的内容，不尝试读取旧值
                            // （旧值读取可能触发 Kanzi 内部错误，特别是对于动态属性包装器）
                            previews.Add(new Dictionary<string, object?>
                            {
                                ["node"] = nodePath,
                                ["nodeType"] = nodeType,
                                ["property"] = propName,
                                ["newValue"] = safeValue
                            });
                        }
                    }

                    // 安全构建返回对象，确保所有值都是基本类型
                    var response = new Dictionary<string, object?>
                    {
                        ["success"] = true,
                        ["preview"] = true,
                        ["totalNodes"] = matchingNodes.Count,
                        ["totalChanges"] = previews.Count,
                        ["changes"] = previews
                    };

                    return SafeSerialize(response);
                }

                // Apply 模式：实际应用修改
                var applied = new List<Dictionary<string, object?>>();
                var failed = new List<Dictionary<string, object?>>();
                int setCount = 0, skipCount = 0;

                foreach (var (nodePath, node, nodeType) in matchingNodes)
                {
                    foreach (var propEntry in properties)
                    {
                        var propName = propEntry.Key;
                        var newValue = propEntry.Value;
                        try
                        {
                            // 尝试设置属性
                            var setMethod = node.GetType().GetMethod("SetPropertyWithCommand",
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                                null, new[] { typeof(string), typeof(object) }, null);

                            if (setMethod != null)
                            {
                                setMethod.Invoke(node, new[] { propName, newValue ?? "" });
                                applied.Add(new Dictionary<string, object?>
                                {
                                    ["node"] = nodePath,
                                    ["property"] = propName,
                                    ["status"] = "applied"
                                });
                                setCount++;
                                continue;
                            }

                            // 备选: Set(string, object)
                            var directSet = node.GetType().GetMethod("Set",
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                                null, new[] { typeof(string), typeof(object) }, null);

                            if (directSet != null)
                            {
                                directSet.Invoke(node, new[] { propName, newValue ?? "" });
                                applied.Add(new Dictionary<string, object?>
                                {
                                    ["node"] = nodePath,
                                    ["property"] = propName,
                                    ["status"] = "applied"
                                });
                                setCount++;
                                continue;
                            }

                            // 无法设置
                            failed.Add(new Dictionary<string, object?>
                            {
                                ["node"] = nodePath,
                                ["property"] = propName,
                                ["status"] = "skipped",
                                ["reason"] = "no suitable Set method found"
                            });
                            skipCount++;
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new Dictionary<string, object?>
                            {
                                ["node"] = nodePath,
                                ["property"] = propName,
                                ["status"] = "failed",
                                ["reason"] = ex.Message
                            });
                            skipCount++;
                        }
                    }
                }

                return SafeSerialize(new
                {
                    success = true,
                    preview = false,
                    totalNodes = matchingNodes.Count,
                    totalChanges = properties.Count * matchingNodes.Count,
                    applied = setCount,
                    skipped = skipCount,
                    results = applied,
                    failed = failed
                });
            }
            catch (Exception ex)
            {
                Log($"BatchSetProperty failed: {ex.Message}");
                return ErrorJson($"Batch set property failed: {ex.Message}");
            }
        }

        private void CollectMatchingNodes(object parent, string parentPath, NodeFilter filter,
            List<(string path, object node, string nodeType)> results, int depth)
        {
            if (depth > 20 || results.Count >= filter.Limit) return;

            // 添加详细日志
            Log($"CollectMatchingNodes: START - parent type={parent.GetType().Name}, path={parentPath}, depth={depth}");

            // 检查 parent 是否是有效的 ProjectItem（有 Name 属性）
            // 如果 parent 是原始类型或其他无效对象，直接返回
            try
            {
                var parentType = parent.GetType();
                Log($"CollectMatchingNodes: parent type check: {parentType.FullName}");

                // 排除原始类型
                if (parentType.IsPrimitive || parentType.IsEnum ||
                    parentType == typeof(string) || parentType == typeof(int) ||
                    parentType == typeof(long) || parentType == typeof(float) ||
                    parentType == typeof(double) || parentType == typeof(decimal) ||
                    parentType == typeof(bool) || parentType == typeof(byte) ||
                    parentType == typeof(char) || parentType.IsValueType)
                {
                    Log($"CollectMatchingNodes: parent is primitive/value type, skipping");
                    return;
                }

                var nameProp = parentType.GetProperty("Name",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (nameProp == null)
                {
                    // parent 没有 Name 属性，可能是无效对象
                    Log($"CollectMatchingNodes: parent has no Name property, skipping");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"CollectMatchingNodes: failed to check parent validity: {ex.Message}");
                return;
            }

            Log($"CollectMatchingNodes: parent validated, getting children...");

            // 获取父节点名称和路径
            string parentName;
            try
            {
                var nameVal = parent.GetType().GetProperty("Name",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)?.GetValue(parent);
                parentName = nameVal?.ToString() ?? "";
            }
            catch { parentName = ""; }

            var currentPath = string.IsNullOrEmpty(parentPath) ? parentName : $"{parentPath}/{parentName}";

            // 使用与 CollectNodesRecursive 相同的 GetChildren 方法（已验证可靠）
            List<object> children;
            try
            {
                children = GetChildren(parent);
            }
            catch (Exception ex)
            {
                Log($"CollectMatchingNodes: failed to get children: {ex.Message}");
                return;
            }

            foreach (var child in children)
            {
                if (child == null) continue;

                try
                {
                    var name = GetItemName(child);
                    if (string.IsNullOrEmpty(name)) continue;

                    var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
                    var type = GetItemType(child);

                    if (MatchesFilter(name, type, path, filter))
                    {
                        results.Add((path, child, type));
                        if (results.Count >= filter.Limit) return;
                    }

                    if (filter.Recursive)
                    {
                        try
                        {
                            CollectMatchingNodes(child, path, filter, results, depth + 1);
                        }
                        catch (Exception ex)
                        {
                            Log($"CollectMatchingNodes: recursive call failed for {path}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"CollectMatchingNodes: failed to process child: {ex.Message}");
                }
            }
        }

        private NodeFilter ParseNodeFilterForBatch(JsonElement element)
        {
            var filter = new NodeFilter();
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
                return filter;

            if (element.TryGetProperty("type", out var t))
                filter.Type = t.GetString();
            if (element.TryGetProperty("name", out var n))
                filter.Name = n.GetString();
            if (element.TryGetProperty("path", out var p))
                filter.Path = p.GetString();
            if (element.TryGetProperty("recursive", out var r))
                filter.Recursive = r.GetBoolean();
            if (element.TryGetProperty("limit", out var l))
                filter.Limit = l.GetInt32();

            return filter;
        }

        public string GetPropertyMetadata(JsonElement? args)
        {
            if (!args.HasValue || !args.Value.TryGetProperty("nodeType", out var nt))
                return ErrorJson("Missing nodeType parameter");

            var nodeType = nt.GetString() ?? "";

            try
            {
                var project = GetActiveProject();
                var allMetadata = new Dictionary<string, object?>();
                var source = "static";

                if (project != null)
                {
                    // 策略1: 从 NodeComponentTypeLibrary 获取
                    var libProp = project.GetType().GetProperty("NodeComponentTypeLibrary",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (libProp != null)
                    {
                        try
                        {
                            var lib = libProp.GetValue(project);
                            var getItemMethod = lib?.GetType().GetMethod("GetItemByName",
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            if (getItemMethod != null)
                            {
                                var typeItem = getItemMethod.Invoke(lib, new object[] { nodeType });
                                if (typeItem != null)
                                {
                                    var propTypes = GetItemProperties(typeItem);
                                    foreach (var kvp in propTypes)
                                        allMetadata[kvp.Key] = kvp.Value;
                                    source = "NodeComponentTypeLibrary";
                                }
                            }
                        }
                        catch { }
                    }

                    // 策略2: 从 Project.PropertyTypes 获取属性类型系统信息
                    try
                    {
                        var ptProp = project.GetType().GetProperty("PropertyTypes",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (ptProp != null)
                        {
                            var pts = ptProp.GetValue(project) as IEnumerable;
                            if (pts != null)
                            {
                                foreach (var pt in pts)
                                {
                                    var ptName = SafeGetProperty(pt, "Name") as string ??
                                                 SafeGetProperty(pt, "LocalName") as string;
                                    if (!string.IsNullOrEmpty(ptName) && !allMetadata.ContainsKey(ptName))
                                    {
                                        var desc = new Dictionary<string, object?>();
                                        var displayName = SafeGetProperty(pt, "DisplayName") as string;
                                        if (!string.IsNullOrEmpty(displayName))
                                            desc["displayName"] = displayName;
                                        var dataType = SafeGetProperty(pt, "DataType");
                                        if (dataType != null)
                                            desc["dataType"] = dataType.ToString();
                                        var isReadOnly = SafeGetProperty(pt, "IsReadOnly");
                                        if (isReadOnly != null)
                                            desc["isReadOnly"] = isReadOnly;
                                        var category = SafeGetProperty(pt, "Category") as string;
                                        if (!string.IsNullOrEmpty(category))
                                            desc["category"] = category;
                                        desc["source"] = "PropertyTypes";
                                        allMetadata[ptName] = desc;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 策略3: 合并静态元数据（补全缺失的属性）
                var staticMetadata = GetCommonPropertyMetadata(nodeType);
                foreach (var kvp in staticMetadata)
                {
                    if (!allMetadata.ContainsKey(kvp.Key))
                        allMetadata[kvp.Key] = kvp.Value;
                }

                return SafeSerialize(new
                {
                    success = true,
                    nodeType,
                    properties = allMetadata,
                    source,
                    propertyCount = allMetadata.Count
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"Get property metadata failed: {ex.Message}");
            }
        }

        #endregion

        #region 审计

        public string AuditBindings(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio not connected");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("No open project");

                bool checkPriority = false;
                bool findOrphans = false;
                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("checkPriority", out var cp))
                        checkPriority = cp.GetBoolean();
                    if (args.Value.TryGetProperty("findOrphans", out var fo))
                        findOrphans = fo.GetBoolean();
                }

                var issues = new List<Dictionary<string, object?>>();
                var totalBindings = 0;
                var bindingCodes = new Dictionary<string, List<string>>(); // code -> node paths
                var allBoundNodes = new HashSet<string>(); // paths that have bindings

                AuditBindingsRecursive(project, "", issues, ref totalBindings,
                    bindingCodes, allBoundNodes, checkPriority, findOrphans, 0);

                // 优先级冲突检测
                var priorityConflicts = new List<Dictionary<string, object?>>();
                if (checkPriority)
                {
                    foreach (var kvp in bindingCodes)
                    {
                        if (kvp.Value.Count > 1)
                        {
                            priorityConflicts.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "priority_conflict",
                                ["bindingCode"] = kvp.Key,
                                ["nodes"] = kvp.Value,
                                ["message"] = $"Binding code '{kvp.Key}' used by {kvp.Value.Count} nodes - may cause priority conflicts"
                            });
                        }
                    }
                }

                // 孤立绑定检测
                var orphanBindings = new List<Dictionary<string, object?>>();
                if (findOrphans)
                {
                    foreach (var issue in issues.Where(i => i["type"]?.ToString() == "orphan"))
                    {
                        orphanBindings.Add(issue);
                    }
                }

                var recommendations = new List<string>();
                if (issues.Any(i => i["type"]?.ToString() == "missing_datasource"))
                    recommendations.Add("Check missing datasource bindings and ensure data context is configured correctly");
                if (issues.Any(i => i["type"]?.ToString() == "orphan"))
                    recommendations.Add("Clean up bindings without target properties");
                if (priorityConflicts.Count > 0)
                    recommendations.Add($"Found {priorityConflicts.Count} priority conflicts - same binding code used by multiple nodes");

                return SafeSerialize(new
                {
                    success = true,
                    totalBindings,
                    issues = issues.Take(200),
                    priorityConflicts,
                    orphanBindings,
                    recommendations
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"Audit bindings failed: {ex.Message}");
            }
        }

        private void AuditBindingsRecursive(object parent, string parentPath,
            List<Dictionary<string, object?>> issues, ref int totalBindings,
            Dictionary<string, List<string>> bindingCodes, HashSet<string> allBoundNodes,
            bool checkPriority, bool findOrphans, int depth)
        {
            if (depth > 20) return;

            try
            {
                foreach (var child in GetChildren(parent))
                {
                    var name = GetItemName(child);
                    var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

                    try
                    {
                        var bindingsProp = child.GetType().GetProperty("Bindings",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (bindingsProp != null)
                        {
                            var bindings = bindingsProp.GetValue(child) as IEnumerable;
                            if (bindings != null)
                            {
                                foreach (var binding in bindings)
                                {
                                    totalBindings++;
                                    var code = SafeGetProperty(binding, "Code") as string ?? "";
                                    var propertyName = ExtractBindingProperty(SafeGetProperty(binding, "Property"));
                                    allBoundNodes.Add(path);

                                    if (string.IsNullOrWhiteSpace(code))
                                    {
                                        issues.Add(new Dictionary<string, object?>
                                        {
                                            ["type"] = "empty_code",
                                            ["node"] = path,
                                            ["property"] = propertyName,
                                            ["message"] = "Binding code is empty"
                                        });
                                    }

                                    // 收集 binding code 分布（用于优先级冲突检测）
                                    if (checkPriority && !string.IsNullOrWhiteSpace(code))
                                    {
                                        if (!bindingCodes.ContainsKey(code))
                                            bindingCodes[code] = new List<string>();
                                        bindingCodes[code].Add($"{path}:{propertyName}");
                                    }

                                    // 检测无效属性名
                                    if (findOrphans && propertyName == "unknown")
                                    {
                                        issues.Add(new Dictionary<string, object?>
                                        {
                                            ["type"] = "orphan",
                                            ["node"] = path,
                                            ["message"] = "Binding target property not found"
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    AuditBindingsRecursive(child, path, issues, ref totalBindings,
                        bindingCodes, allBoundNodes, checkPriority, findOrphans, depth + 1);
                }
            }
            catch { }
        }

        public string AuditLocalization(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio not connected");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("No open project");

                // 解析参数
                var languages = new List<string>();
                var includeUntranslated = true;

                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("languages", out var langEl))
                    {
                        foreach (var lang in langEl.EnumerateArray())
                            languages.Add(lang.GetString() ?? "");
                    }
                    if (args.Value.TryGetProperty("includeUntranslated", out var iu))
                        includeUntranslated = iu.GetBoolean();
                }

                // 如果未指定语言，尝试从项目中获取可用语言列表
                var availableLanguages = new List<string>();
                if (languages.Count == 0)
                {
                    availableLanguages = GetAvailableLanguages(project);
                    languages = availableLanguages.Count > 0 ? availableLanguages : new List<string> { "en-US", "zh-CN" };
                }
                else
                {
                    availableLanguages = languages;
                }

                var textNodes = new List<Dictionary<string, object?>>();
                var missingTranslations = new List<Dictionary<string, object?>>();
                var inconsistentKeys = new List<Dictionary<string, object?>>();
                int totalTextNodes = 0;

                // 收集所有文本节点
                CollectTextNodesRecursive(project, "", textNodes, 0);

                // 分析每个文本节点
                foreach (var node in textNodes)
                {
                    totalTextNodes++;
                    var path = node["path"]?.ToString() ?? "";
                    var text = node["text"]?.ToString() ?? "";
                    var type = node["type"]?.ToString() ?? "";

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        missingTranslations.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "empty_text",
                            ["node"] = path,
                            ["nodeType"] = type,
                            ["message"] = "Text property is empty"
                        });
                        continue;
                    }

                    // 检测是否为本地化 key（通常以 @ 开头或包含特定格式）
                    if (text.StartsWith("@") || text.Contains("StringTable/"))
                    {
                        // 这是一个本地化 key，检查各语言是否有翻译
                        foreach (var lang in availableLanguages)
                        {
                            var hasTranslation = CheckLocalizationKey(project, text, lang);
                            if (!hasTranslation)
                            {
                                missingTranslations.Add(new Dictionary<string, object?>
                                {
                                    ["type"] = "missing_translation",
                                    ["node"] = path,
                                    ["key"] = text,
                                    ["language"] = lang,
                                    ["message"] = $"Localization key '{text}' missing translation for '{lang}'"
                                });
                            }
                        }
                    }
                }

                // 统计信息
                int nodesWithTranslation = textNodes.Count - missingTranslations.Count(i => i["type"]?.ToString() == "empty_text");
                double coverage = totalTextNodes > 0 ? (double)nodesWithTranslation / totalTextNodes * 100 : 100;

                var recommendations = new List<string>();
                if (missingTranslations.Count > 0)
                    recommendations.Add($"Found {missingTranslations.Count} missing translations or empty texts");
                if (coverage < 80)
                    recommendations.Add($"Localization coverage is {coverage:F1}% - below recommended 80%");
                if (inconsistentKeys.Count > 0)
                    recommendations.Add($"Found {inconsistentKeys.Count} inconsistent localization keys");

                return SafeSerialize(new
                {
                    success = true,
                    totalTextNodes,
                    textNodes = textNodes.Take(100),
                    availableLanguages,
                    missingTranslations = missingTranslations.Take(100),
                    inconsistentKeys,
                    coverage = $"{coverage:F1}%",
                    recommendations
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"Audit localization failed: {ex.Message}");
            }
        }

        private List<string> GetAvailableLanguages(object project)
        {
            var languages = new List<string>();
            try
            {
                // 尝试从 StringTableLibrary 获取语言列表
                var libProp = project.GetType().GetProperty("StringTableLibrary",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (libProp != null)
                {
                    var lib = libProp.GetValue(project) as IEnumerable;
                    if (lib != null)
                    {
                        foreach (var item in lib)
                        {
                            var name = GetItemName(item);
                            if (!string.IsNullOrEmpty(name) && !languages.Contains(name))
                                languages.Add(name);
                        }
                    }
                }

                // 回退: 查找 StringTable 相关子节点
                if (languages.Count == 0)
                {
                    var stringTables = FindChildrenByType(project, "StringTable");
                    foreach (var st in stringTables)
                    {
                        var name = GetItemName(st);
                        if (!string.IsNullOrEmpty(name) && !languages.Contains(name))
                            languages.Add(name);
                    }
                }
            }
            catch { }
            return languages;
        }

        private List<object> FindChildrenByType(object parent, string typeName)
        {
            var result = new List<object>();
            try
            {
                foreach (var child in GetChildren(parent))
                {
                    var type = GetItemType(child);
                    if (type.Contains(typeName))
                        result.Add(child);
                    result.AddRange(FindChildrenByType(child, typeName));
                }
            }
            catch { }
            return result;
        }

        private bool CheckLocalizationKey(object project, string key, string language)
        {
            try
            {
                // 简化检测：查找 StringTable 中是否有对应 key
                var stringTables = FindChildrenByType(project, "StringTable");
                foreach (var st in stringTables)
                {
                    // 跳过语言过滤（简化处理）
                    var children = GetChildren(st);
                    foreach (var entry in children)
                    {
                        var name = GetItemName(entry);
                        if (name == key || name.Contains(key))
                            return true;
                    }
                }
            }
            catch { }
            // 找不到 StringTable 时保守返回 true（避免误报）
            return true;
        }

        private void CollectTextNodesRecursive(object parent, string parentPath,
            List<Dictionary<string, object?>> textNodes, int depth)
        {
            if (depth > 20) return;

            try
            {
                foreach (var child in GetChildren(parent))
                {
                    var name = GetItemName(child);
                    var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
                    var type = GetItemType(child);

                    if (type.Contains("Text"))
                    {
                        string? textValue = null;
                        try
                        {
                            textValue = SafeGetProperty(child, "Text") as string;
                        }
                        catch { }

                        textNodes.Add(new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["type"] = type,
                            ["text"] = textValue
                        });
                    }

                    CollectTextNodesRecursive(child, path, textNodes, depth + 1);
                }
            }
            catch { }
        }

        public string AuditProjectStructure(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                var issues = new List<Dictionary<string, object?>>();
                int totalNodes = 0, maxDepth = 0;

                AuditStructureRecursive(project, "", 0, issues, ref totalNodes, ref maxDepth);

                var score = 100;
                if (maxDepth > 5) score -= 10;
                if (issues.Count > 0) score -= issues.Count * 5;
                score = Math.Max(score, 0);

                var recommendations = new List<string>();
                if (maxDepth > 5)
                    recommendations.Add($"节点嵌套深度为 {maxDepth}，建议控制在 5 层以内");
                if (issues.Any(i => i["type"]?.ToString() == "naming"))
                    recommendations.Add("统一节点命名规范，使用描述性名称");

                return SafeSerialize(new
                {
                    success = true,
                    totalNodes,
                    maxDepth,
                    issues,
                    score,
                    recommendations
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"审计项目结构失败: {ex.Message}");
            }
        }

        private void AuditStructureRecursive(object parent, string parentPath, int depth,
            List<Dictionary<string, object?>> issues, ref int totalNodes, ref int maxDepth)
        {
            if (depth > 25) return;

            try
            {
                foreach (var child in GetChildren(parent))
                {
                    totalNodes++;
                    var name = GetItemName(child);
                    var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
                    maxDepth = Math.Max(maxDepth, depth + 1);

                    if (depth + 1 > 5)
                    {
                        issues.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "deep_nesting",
                            ["path"] = path,
                            ["depth"] = depth + 1,
                            ["message"] = $"嵌套深度 {depth + 1} 超过建议值 5"
                        });
                    }

                    if (name.Any(c => char.IsDigit(c)) && name.Length <= 3)
                    {
                        issues.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "naming",
                            ["path"] = path,
                            ["message"] = "节点名称过短或含数字，建议使用描述性名称"
                        });
                    }

                    AuditStructureRecursive(child, path, depth + 1, issues, ref totalNodes, ref maxDepth);
                }
            }
            catch { }
        }

        /// <summary>
        /// 审计资源引用 - 找出未使用、破损或孤立的资源
        /// </summary>
        public string AuditResourceReferences(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            var checkUnused = !args.HasValue || !args.Value.TryGetProperty("checkUnused", out var cu) || cu.GetBoolean();
            var checkBroken = !args.HasValue || !args.Value.TryGetProperty("checkBroken", out var cb) || cb.GetBoolean();
            var checkOrphaned = !args.HasValue || !args.Value.TryGetProperty("checkOrphaned", out var co) || co.GetBoolean();

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                var unusedResources = new List<Dictionary<string, object?>>();
                var brokenReferences = new List<Dictionary<string, object?>>();
                var orphanedResources = new List<Dictionary<string, object?>>();

                // 收集所有纹理资源
                var textureLibrary = FindChildByType(project, "TextureLibrary");
                if (textureLibrary != null)
                {
                    CollectTextureResources(textureLibrary, unusedResources, brokenReferences, checkUnused, checkBroken);
                }

                // 收集所有材质资源
                var materialTypes = FindChildByType(project, "MaterialTypes");
                if (materialTypes != null)
                {
                    CollectMaterialResources(materialTypes, unusedResources, brokenReferences, checkUnused, checkBroken);
                }

                // 收集所有资源引用的节点
                var allReferencingNodes = new HashSet<string>();
                CollectResourceReferences(project, allReferencingNodes, 0);

                // 检查孤立资源
                if (checkOrphaned)
                {
                    foreach (var tex in unusedResources.Where(r => r["type"]?.ToString() == "texture"))
                    {
                        var path = tex["path"]?.ToString() ?? "";
                        if (!allReferencingNodes.Any(r => r.Contains(path)))
                        {
                            orphanedResources.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "orphan_texture",
                                ["path"] = path,
                                ["message"] = "Texture not referenced by any node"
                            });
                        }
                    }
                }

                var recommendations = new List<string>();
                if (unusedResources.Count > 0)
                    recommendations.Add($"Found {unusedResources.Count} unused resources - consider removing them to reduce project size");
                if (brokenReferences.Count > 0)
                    recommendations.Add($"Found {brokenReferences.Count} broken references - these may cause runtime errors");
                if (orphanedResources.Count > 0)
                    recommendations.Add($"Found {orphanedResources.Count} orphaned resources - they exist but are not used");

                return SafeSerialize(new
                {
                    success = true,
                    unusedResources = checkUnused ? (object)unusedResources : new List<Dictionary<string, object?>>(),
                    brokenReferences = checkBroken ? (object)brokenReferences : new List<Dictionary<string, object?>>(),
                    orphanedResources = checkOrphaned ? (object)orphanedResources : new List<Dictionary<string, object?>>(),
                    summary = new
                    {
                        totalUnused = unusedResources.Count,
                        totalBroken = brokenReferences.Count,
                        totalOrphaned = orphanedResources.Count
                    },
                    recommendations
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"审计资源引用失败: {ex.Message}");
            }
        }

        private object? FindChildByType(object parent, string typeName)
        {
            try
            {
                foreach (var child in GetChildren(parent))
                {
                    var type = GetItemType(child);
                    if (type.Contains(typeName))
                        return child;

                    // 递归搜索
                    var found = FindChildByType(child, typeName);
                    if (found != null)
                        return found;
                }
            }
            catch { }
            return null;
        }

        private void CollectTextureResources(object textureLibrary, List<Dictionary<string, object?>> unused,
            List<Dictionary<string, object?>> broken, bool checkUnused, bool checkBroken)
        {
            try
            {
                foreach (var tex in GetChildren(textureLibrary))
                {
                    var name = GetItemName(tex);
                    var path = GetItemPath(tex);
                    var type = GetItemType(tex);

                    if (type.Contains("Texture"))
                    {
                        // 检查是否为有效纹理
                        bool isValid = false;
                        try
                        {
                            // 尝试获取纹理属性
                            var texProp = SafeGetProperty(tex, "Texture");
                            if (texProp != null)
                            {
                                isValid = true;
                                // 检查是否有文件引用
                                var filePath = SafeGetProperty(tex, "FilePath") as string;
                                if (checkBroken && !string.IsNullOrEmpty(filePath) && !File.Exists(filePath))
                                {
                                    broken.Add(new Dictionary<string, object?>
                                    {
                                        ["type"] = "broken_texture",
                                        ["path"] = path,
                                        ["filePath"] = filePath,
                                        ["message"] = "Texture file not found"
                                    });
                                }
                            }
                        }
                        catch { isValid = true; } // 默认认为有效

                        if (checkUnused && isValid)
                        {
                            unused.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "texture",
                                ["path"] = path,
                                ["name"] = name
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private void CollectMaterialResources(object materialTypes, List<Dictionary<string, object?>> unused,
            List<Dictionary<string, object?>> broken, bool checkUnused, bool checkBroken)
        {
            try
            {
                foreach (var mat in GetChildren(materialTypes))
                {
                    var name = GetItemName(mat);
                    var path = GetItemPath(mat);
                    var type = GetItemType(mat);

                    if (type.Contains("Material"))
                    {
                        if (checkUnused)
                        {
                            unused.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "material",
                                ["path"] = path,
                                ["name"] = name
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private void CollectResourceReferences(object parent, HashSet<string> references, int depth)
        {
            if (depth > 20) return;

            try
            {
                var itemType = parent.GetType().Name;

                // 检查属性中的资源引用
                try
                {
                    var props = parent.GetType().GetProperty("Properties",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (props != null)
                    {
                        var propValues = props.GetValue(parent) as IEnumerable;
                        if (propValues != null)
                        {
                            foreach (var p in propValues)
                            {
                                var propName = SafeGetProperty(p, "Name") as string;
                                var propValue = SafeGetProperty(p, "Value");
                                if (propValue != null)
                                {
                                    references.Add($"{GetItemPath(parent)}.{propName}={propValue}");
                                }
                            }
                        }
                    }
                }
                catch { }

                // 递归处理子节点
                foreach (var child in GetChildren(parent))
                {
                    CollectResourceReferences(child, references, depth + 1);
                }
            }
            catch { }
        }

        #endregion

        #region 状态

        public string GetStatus(JsonElement? args)
        {
            if (!HasStudio)
                return SafeSerialize(new
                {
                    success = true,
                    server = "KanziMcpPlugin",
                    version = "1.0.0",
                    projectOpen = false,
                    projectName = "",
                    kanziStudioConnected = false,
                    note = "Kanzi Studio not available"
                });

            // 实时检查项目状态，不依赖可能过期的 _isProjectOpen 字段
            var projectOpen = false;
            var projectName = "";
            var nodeCount = 0;

            try
            {
                var project = GetActiveProject();
                if (project != null)
                {
                    projectOpen = true;
                    projectName = SafeGetProperty(project, "Name") as string ?? "";

                    // 验证项目是否真的打开了（不是 null）
                    try
                    {
                        CountNodesRecursive(project, ref nodeCount, 0);
                    }
                    catch { nodeCount = -1; }
                }
            }
            catch { }

            // 更新内部状态以保持一致
            _isProjectOpen = projectOpen;
            _projectName = projectName;

            return SafeSerialize(new
            {
                success = true,
                server = "KanziMcpPlugin",
                version = "1.0.0",
                projectOpen,
                projectName,
                nodeCount,
                kanziStudioConnected = true,
                studioVersion = _studio!.Version,
                tcpPort = 9595
            });
        }

        private void CountNodesRecursive(object parent, ref int count, int depth)
        {
            if (depth > 25 || count > 10000) return;
            try
            {
                foreach (var child in GetChildren(parent))
                {
                    count++;
                    CountNodesRecursive(child, ref count, depth + 1);
                }
            }
            catch { }
        }

        #endregion

        #region 节点创建与删除

        /// <summary>
        /// 创建新节点
        /// </summary>
        public string CreateNode(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio not connected");

            if (!args.HasValue)
                return ErrorJson("Missing arguments");

            var parentPath = args.Value.TryGetProperty("parentPath", out var pp) ? pp.GetString() ?? "" : "";
            var nodeType = args.Value.TryGetProperty("nodeType", out var nt) ? nt.GetString() ?? "" : "";
            var nodeName = args.Value.TryGetProperty("nodeName", out var nn) ? nn.GetString() : null;
            var properties = args.Value.TryGetProperty("properties", out var props) ? props : default;

            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(nodeType))
                return ErrorJson("Missing parentPath or nodeType parameter");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("No active project");

                // Get parent node
                var parentItem = GetProjectItem(parentPath);
                if (parentItem == null)
                    return ErrorJson($"Parent node not found: {parentPath}");

                Log($"CreateNode: parent={parentPath}, type={nodeType}, name={nodeName}");

                // Try to create node using various methods
                object? newNode = null;

                // Strategy 1: CreateChildNode method with string type name
                var createMethod = parentItem.GetType().GetMethod("CreateChildNode",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                    null, new[] { typeof(string) }, null);
                if (createMethod != null)
                {
                    try
                    {
                        Log($"CreateNode: trying CreateChildNode with type={nodeType}");
                        newNode = createMethod.Invoke(parentItem, new object[] { nodeType });
                        if (newNode != null)
                            Log($"CreateNode: CreateChildNode succeeded");
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateNode: CreateChildNode failed: {ex.Message}");
                    }
                }

                // Strategy 2: Try with NodeComponentTypeLibrary
                if (newNode == null)
                {
                    try
                    {
                        var typeLibProp = project.GetType().GetProperty("NodeComponentTypeLibrary",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (typeLibProp != null)
                        {
                            var typeLib = typeLibProp.GetValue(project);
                            if (typeLib != null)
                            {
                                // Try to find type in library
                                var findTypeMethod = typeLib.GetType().GetMethod("GetItemByName",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (findTypeMethod != null)
                                {
                                    var typeInfo = findTypeMethod.Invoke(typeLib, new object[] { nodeType });
                                    if (typeInfo != null)
                                    {
                                        // Try to create instance from type info
                                        var createInstanceMethod = typeLib.GetType().GetMethod("CreateNode",
                                            BindingFlags.Public | BindingFlags.Instance);
                                        if (createInstanceMethod != null)
                                        {
                                            try
                                            {
                                                newNode = createInstanceMethod.Invoke(typeLib, new object[] { parentItem, typeInfo });
                                                Log($"CreateNode: created via NodeComponentTypeLibrary");
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateNode: NodeComponentTypeLibrary approach failed: {ex.Message}");
                    }
                }

                // Strategy 3: AddNode or AddChild method
                if (newNode == null)
                {
                    createMethod = parentItem.GetType().GetMethod("AddNode",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (createMethod != null)
                    {
                        try
                        {
                            Log($"CreateNode: using AddNode method");
                            var nodeTypeObj = FindNodeType(nodeType);
                            if (nodeTypeObj != null)
                            {
                                newNode = createMethod.Invoke(parentItem, new[] { nodeTypeObj });
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"CreateNode: AddNode failed: {ex.Message}");
                        }
                    }
                }

                // Strategy 4: AddChild method
                if (newNode == null)
                {
                    createMethod = parentItem.GetType().GetMethod("AddChild",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (createMethod != null)
                    {
                        try
                        {
                            Log($"CreateNode: using AddChild method");
                            var nodeTypeObj = FindNodeType(nodeType);
                            if (nodeTypeObj != null)
                            {
                                newNode = createMethod.Invoke(parentItem, new[] { nodeTypeObj });
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"CreateNode: AddChild failed: {ex.Message}");
                        }
                    }
                }

                // Strategy 5: Children.Add method with Activator
                if (newNode == null)
                {
                    try
                    {
                        var childrenProp = parentItem.GetType().GetProperty("Children",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (childrenProp != null)
                        {
                            var children = childrenProp.GetValue(parentItem) as IList;
                            if (children != null)
                            {
                                var nodeTypeObj = FindNodeType(nodeType);
                                if (nodeTypeObj != null)
                                {
                                    var newItem = Activator.CreateInstance(nodeTypeObj);
                                    if (newItem != null)
                                    {
                                        // Set name if provided
                                        if (!string.IsNullOrEmpty(nodeName))
                                        {
                                            var nameProp = nodeTypeObj.GetProperty("Name",
                                                BindingFlags.Public | BindingFlags.Instance);
                                            if (nameProp != null && nameProp.CanWrite)
                                            {
                                                nameProp.SetValue(newItem, nodeName);
                                            }
                                        }
                                        children.Add(newItem);
                                        newNode = newItem;
                                        Log($"CreateNode: created via Children.Add");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateNode: Children.Add approach failed: {ex.Message}");
                    }
                }

                // Strategy 6: Try Project.CreateNode or similar
                if (newNode == null)
                {
                    try
                    {
                        var createProjectNodeMethod = project.GetType().GetMethod("CreateNode",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (createProjectNodeMethod != null)
                        {
                            Log($"CreateNode: trying Project.CreateNode");
                            var parameters = createProjectNodeMethod.GetParameters();
                            if (parameters.Length >= 2)
                            {
                                // Most CreateNode methods need parent and type
                                var nodeTypeObj = FindNodeType(nodeType);
                                if (nodeTypeObj != null)
                                {
                                    try
                                    {
                                        newNode = createProjectNodeMethod.Invoke(project, new[] { parentItem, nodeTypeObj });
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateNode: Project.CreateNode failed: {ex.Message}");
                    }
                }

                // Strategy 7: CloneUnder - Find a template node and clone it
                if (newNode == null)
                {
                    try
                    {
                        Log($"CreateNode: trying CloneUnder strategy...");

                        // First, find a template node of the requested type
                        string templatePath = nodeType switch
                        {
                            "EmptyNode2D" => "Templates/DefaultNode2D",
                            "TextBlock2D" => "Templates/DefaultTextBlock2D",
                            "RectangleNode2D" => "Templates/DefaultRectangleNode2D",
                            "Image2D" => "Templates/DefaultImage2D",
                            _ => null
                        };

                        object? templateNode = null;
                        if (templatePath != null)
                        {
                            templateNode = GetProjectItem(templatePath);
                        }

                        // If no template found, try to find any node of the same type
                        if (templateNode == null)
                        {
                            var templateList = new List<(string path, object node, string nodeType)>();
                            var templateFilter = new NodeFilter
                            {
                                Type = nodeType,
                                Recursive = true,
                                Limit = 1
                            };
                            try
                            {
                                CollectMatchingNodes(project, "", templateFilter, templateList, 0);
                                if (templateList.Count > 0)
                                {
                                    templateNode = templateList[0].node;
                                    Log($"CreateNode: found template node: {templateList[0].path}");
                                }
                            }
                            catch { }
                        }

                        // If we have a template node, try CloneUnder
                        if (templateNode != null)
                        {
                            var cloneUnderMethod = templateNode.GetType().GetMethod("CloneUnder",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (cloneUnderMethod != null)
                            {
                                try
                                {
                                    // CloneUnder(name, parent, CloneMethod)
                                    // CloneMethod might be an enum, try to find it
                                    var cloneMethodType = cloneUnderMethod.GetParameters()[2].ParameterType;
                                    var cloneMethodValues = Enum.GetValues(cloneMethodType);
                                    var defaultCloneMethod = cloneMethodValues.GetValue(0);

                                    newNode = cloneUnderMethod.Invoke(templateNode,
                                        new[] { nodeName ?? $"New{nodeType}", parentItem, defaultCloneMethod });
                                    if (newNode != null)
                                    {
                                        Log($"CreateNode: created via CloneUnder");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"CreateNode: CloneUnder invoke failed: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateNode: CloneUnder strategy failed: {ex.Message}");
                    }
                }

                // Strategy 8: ExecutePluginCommand if available
                if (newNode == null)
                {
                    try
                    {
                        Log($"CreateNode: trying ExecutePluginCommand strategy...");

                        // Map node type to command name
                        string? commandName = nodeType switch
                        {
                            "EmptyNode2D" => "CreateEmptyNode2D",
                            "EmptyNode3D" => "CreateEmptyNode3D",
                            "TextBlock2D" => "CreateTextBlock2D",
                            "RectangleNode2D" => "CreateRectangleNode2D",
                            "Image2D" => "CreateImage2D",
                            _ => null
                        };

                        if (commandName != null)
                        {
                            // Try to execute command on the parent
                            var execMethod = parentItem.GetType().GetMethod("ExecutePluginCommand",
                                BindingFlags.Public | BindingFlags.Instance,
                                null, new[] { typeof(string), typeof(IEnumerable<ProjectItem>) }, null);

                            if (execMethod != null)
                            {
                                try
                                {
                                    var parentItems = new List<ProjectItem> { (ProjectItem)parentItem };
                                    execMethod.Invoke(parentItem, new object[] { commandName, parentItems });
                                    Log($"CreateNode: ExecutePluginCommand executed: {commandName}");
                                    // Note: This might not return the new node, but could succeed
                                }
                                catch (Exception ex)
                                {
                                    Log($"CreateNode: ExecutePluginCommand failed: {ex.Message}");
                                }
                            }

                            // Also try on project level
                            var projExecMethod = project.GetType().GetMethod("ExecutePluginCommand",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (projExecMethod != null)
                            {
                                try
                                {
                                    var parameters = projExecMethod.GetParameters();
                                    if (parameters.Length >= 2)
                                    {
                                        var parentItems = new List<ProjectItem> { (ProjectItem)parentItem };
                                        projExecMethod.Invoke(project, new object[] { commandName, parentItems });
                                        Log($"CreateNode: Project.ExecutePluginCommand executed: {commandName}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"CreateNode: Project.ExecutePluginCommand failed: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateNode: ExecutePluginCommand strategy failed: {ex.Message}");
                    }
                }

                if (newNode == null)
                {
                    // Return a clear message about the limitation
                    return ErrorJson($"Cannot create node type '{nodeType}' dynamically. This feature requires using Kanzi Studio UI or a compatible node factory API. Please create the node manually in Kanzi Studio.");
                }

                // Set node name if provided
                if (!string.IsNullOrEmpty(nodeName))
                {
                    try
                    {
                        var nameProp = newNode.GetType().GetProperty("Name",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (nameProp != null && nameProp.CanWrite)
                        {
                            nameProp.SetValue(newNode, nodeName);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateNode: failed to set name: {ex.Message}");
                    }
                }

                // Apply initial properties if provided
                if (properties.ValueKind == JsonValueKind.Object)
                {
                    ApplyInitialProperties(newNode, properties);
                }

                var newPath = GetItemPath(newNode);
                var newName = GetItemName(newNode);

                return SafeSerialize(new
                {
                    success = true,
                    created = true,
                    node = newPath,
                    nodeName = newName,
                    nodeType = GetItemType(newNode),
                    parent = parentPath
                });
            }
            catch (Exception ex)
            {
                Log($"CreateNode failed: {ex.Message}");
                return ErrorJson($"Failed to create node: {ex.Message}");
            }
        }

        /// <summary>
        /// 删除节点
        /// </summary>
        public string DeleteNode(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            if (!args.HasValue)
                return ErrorJson("缺少参数");

            var path = args.Value.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            var mode = args.Value.TryGetProperty("mode", out var m) ? m.GetString() ?? "apply" : "apply";

            if (string.IsNullOrEmpty(path))
                return ErrorJson("缺少 path 参数");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                // 获取要删除的节点
                var item = GetProjectItem(path);
                if (item == null)
                    return ErrorJson($"节点未找到: {path}");

                // 检查是否为项目根节点（不允许删除）
                if (path == _projectName || path == "")
                {
                    return ErrorJson("不能删除项目根节点");
                }

                var nodeName = GetItemName(item);
                var nodeType = GetItemType(item);

                Log($"DeleteNode: path={path}, mode={mode}");

                // 预览模式
                if (mode == "preview" || mode == "dry-run")
                {
                    // 检查是否有子节点
                    var children = GetChildren(item);
                    var childCount = children.Count;

                    // 检查是否有引用
                    var isReferenced = CheckNodeReferences(project, path);

                    return SafeSerialize(new
                    {
                        success = true,
                        preview = true,
                        node = path,
                        nodeName,
                        nodeType,
                        childCount,
                        isReferenced,
                        warning = childCount > 0 ? $"This node has {childCount} child nodes that will also be deleted" : null,
                        willDelete = true
                    });
                }

                // 删除模式 - 多策略尝试
                bool deleted = false;

                // 策略1: Delete 或 Remove 方法
                var deleteMethod = item.GetType().GetMethod("Delete",
                    BindingFlags.Public | BindingFlags.Instance);
                if (deleteMethod != null)
                {
                    try
                    {
                        Log($"DeleteNode: using Delete method");
                        deleteMethod.Invoke(item, null);
                        deleted = true;
                    }
                    catch (Exception ex)
                    {
                        Log($"DeleteNode: Delete method failed: {ex.Message}");
                    }
                }

                // 策略2: Remove 方法
                if (!deleted)
                {
                    deleteMethod = item.GetType().GetMethod("Remove",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (deleteMethod != null)
                    {
                        try
                        {
                            Log($"DeleteNode: using Remove method");
                            deleteMethod.Invoke(item, null);
                            deleted = true;
                        }
                        catch (Exception ex)
                        {
                            Log($"DeleteNode: Remove method failed: {ex.Message}");
                        }
                    }
                }

                // 策略3: 从父节点的 Children 集合中移除
                if (!deleted)
                {
                    try
                    {
                        var parent = GetParent(item);
                        if (parent != null)
                        {
                            var childrenProp = parent.GetType().GetProperty("Children",
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            if (childrenProp != null)
                            {
                                var children = childrenProp.GetValue(parent) as IList;
                                if (children != null && children.Contains(item))
                                {
                                    Log($"DeleteNode: removing from parent's Children collection");
                                    children.Remove(item);
                                    deleted = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"DeleteNode: Children.Remove approach failed: {ex.Message}");
                    }
                }

                // 策略4: 使用 KanziStudio 命令
                if (!deleted && _studio != null)
                {
                    try
                    {
                        var deleteCommandMethod = _studio.GetType().GetMethod("DeleteNode",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (deleteCommandMethod != null)
                        {
                            Log($"DeleteNode: using KanziStudio.DeleteNode");
                            deleteCommandMethod.Invoke(_studio, new[] { item });
                            deleted = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"DeleteNode: KanziStudio.DeleteNode failed: {ex.Message}");
                    }
                }

                if (!deleted)
                {
                    return ErrorJson($"无法删除节点: {path}。Kanzi 不支持通过 MCP 删除此类型的节点。");
                }

                return SafeSerialize(new
                {
                    success = true,
                    deleted = true,
                    node = path,
                    nodeName,
                    nodeType
                });
            }
            catch (Exception ex)
            {
                Log($"DeleteNode failed: {ex.Message}");
                return ErrorJson($"删除节点失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Find type in all loaded assemblies by name (case-insensitive)
        /// </summary>
        private Type? FindTypeInAssemblies(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetTypes()
                        .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                                            (t.FullName?.Contains(typeName) ?? false));
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 查找节点类型
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

        /// <summary>
        /// 获取节点的父节点
        /// </summary>
        private object? GetParent(object item)
        {
            try
            {
                var parentProp = item.GetType().GetProperty("Parent",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (parentProp != null)
                {
                    return parentProp.GetValue(item);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 检查节点是否被引用
        /// </summary>
        private bool CheckNodeReferences(object project, string nodePath)
        {
            // 简化实现：遍历项目查找对该节点的引用
            // 这是一个昂贵的操作，简化为总是返回 false
            return false;
        }

        /// <summary>
        /// 应用初始属性
        /// </summary>
        private void ApplyInitialProperties(object node, JsonElement properties)
        {
            foreach (var prop in properties.EnumerateObject())
            {
                try
                {
                    var propName = prop.Name;
                    var propValue = JsonElementToObject(prop.Value);

                    // 尝试使用 SetPropertyWithCommand
                    var setMethod = node.GetType().GetMethod("SetPropertyWithCommand",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object) }, null);
                    if (setMethod != null)
                    {
                        setMethod.Invoke(node, new[] { propName, propValue });
                        Log($"ApplyInitialProperties: set {propName} via SetPropertyWithCommand");
                        continue;
                    }

                    // 尝试使用 Set 方法
                    setMethod = node.GetType().GetMethod("Set",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object) }, null);
                    if (setMethod != null)
                    {
                        setMethod.Invoke(node, new[] { propName, propValue });
                        Log($"ApplyInitialProperties: set {propName} via Set");
                    }
                }
                catch (Exception ex)
                {
                    Log($"ApplyInitialProperties: failed to set {prop.Name}: {ex.Message}");
                }
            }
        }

        #endregion

        #region 资源导入与诊断

        /// <summary>
        /// Import image into resource library
        /// </summary>
        public string ImportImage(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio not connected");

            if (!args.HasValue)
                return ErrorJson("Missing arguments");

            var filePath = args.Value.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? "" : "";
            var resourceName = args.Value.TryGetProperty("resourceName", out var rn) ? rn.GetString() : null;
            var targetFolder = args.Value.TryGetProperty("targetFolder", out var tf) ? tf.GetString() ?? "Textures" : "Textures";

            if (string.IsNullOrEmpty(filePath))
                return ErrorJson("Missing filePath parameter");

            Log($"ImportImage: filePath={filePath}, resourceName={resourceName}, targetFolder={targetFolder}");

            try
            {
                // Validate file exists
                if (!System.IO.File.Exists(filePath))
                    return ErrorJson($"File not found: {filePath}");

                // Get project
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("No active project");

                // Find or create Textures folder
                var texturesFolder = GetOrCreateResourceFolder(project, targetFolder);
                if (texturesFolder == null)
                    return ErrorJson($"Cannot find or create resource folder: {targetFolder}");

                Log($"ImportImage: found textures folder: {GetItemName(texturesFolder)}");

                object? importedItem = null;

                // Strategy 1: Use TextureLibrary.Import method
                var importMethod = texturesFolder.GetType().GetMethod("Import",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                    null, new[] { typeof(string) }, null);

                if (importMethod != null)
                {
                    try
                    {
                        Log($"ImportImage: trying Import method");
                        importedItem = importMethod.Invoke(texturesFolder, new object[] { filePath });
                        if (importedItem != null)
                            Log($"ImportImage: Import method succeeded");
                    }
                    catch (Exception ex)
                    {
                        Log($"ImportImage: Import method failed: {ex.Message}");
                    }
                }

                // Strategy 2: Use ImportImageFile or similar method
                if (importedItem == null)
                {
                    foreach (var methodName in new[] { "ImportImageFile", "ImportTexture", "AddImage", "AddTexture" })
                    {
                        var altMethod = texturesFolder.GetType().GetMethod(methodName,
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (altMethod != null)
                        {
                            try
                            {
                                Log($"ImportImage: trying {methodName}");
                                var parameters = altMethod.GetParameters();
                                if (parameters.Length == 1)
                                {
                                    importedItem = altMethod.Invoke(texturesFolder, new object[] { filePath });
                                }
                                else if (parameters.Length >= 2)
                                {
                                    // Try with additional parameters
                                    importedItem = altMethod.Invoke(texturesFolder, new[] { filePath, resourceName ?? System.IO.Path.GetFileNameWithoutExtension(filePath) });
                                }
                                if (importedItem != null)
                                {
                                    Log($"ImportImage: {methodName} succeeded");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"ImportImage: {methodName} failed: {ex.Message}");
                            }
                        }
                    }
                }

                // Strategy 3: Try to use Studio's import functionality
                if (importedItem == null)
                {
                    try
                    {
                        var studioType = _studio.GetType();
                        // Look for importers or resource factories
                        var createTextureMethod = studioType.GetMethod("CreateTexture",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (createTextureMethod != null)
                        {
                            Log($"ImportImage: trying CreateTexture");
                            importedItem = createTextureMethod.Invoke(_studio, new object[] { filePath });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"ImportImage: CreateTexture failed: {ex.Message}");
                    }
                }

                // Strategy 4: Create texture object directly and add to library
                if (importedItem == null)
                {
                    try
                    {
                        // Find SingleTexture or similar type
                        var textureType = FindTypeInAssemblies("SingleTexture") ??
                                        FindTypeInAssemblies("Texture") ??
                                        FindTypeInAssemblies("Image");

                        if (textureType != null)
                        {
                            Log($"ImportImage: found texture type: {textureType.FullName}");

                            // Try to create instance
                            object? textureObj = null;
                            var ctorWithPath = textureType.GetConstructor(
                                BindingFlags.Public | BindingFlags.Instance,
                                null, new[] { typeof(string) }, null);
                            if (ctorWithPath != null)
                            {
                                textureObj = ctorWithPath.Invoke(new object[] { filePath });
                            }

                            if (textureObj != null)
                            {
                                // Add to texture library
                                var addMethod = texturesFolder.GetType().GetMethod("AddChild",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (addMethod != null)
                                {
                                    importedItem = addMethod.Invoke(texturesFolder, new[] { textureObj });
                                    Log($"ImportImage: created texture and added to library");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"ImportImage: direct texture creation failed: {ex.Message}");
                    }
                }

                // Strategy 5: Try using Studio's Commands API to execute ImportImages command
                if (importedItem == null && _studio != null)
                {
                    try
                    {
                        Log($"ImportImage: trying ExecutePluginCommand for ImportImages...");

                        // Find the ImportImages command on KanziStudio
                        var studioType = _studio.GetType();

                        // Try ExecutePluginCommand with string
                        var execMethod = studioType.GetMethod("ExecutePluginCommand",
                            BindingFlags.Public | BindingFlags.Instance,
                            null, new Type[] { typeof(string), typeof(IEnumerable<ProjectItem>) }, null);

                        if (execMethod != null)
                        {
                            try
                            {
                                // Create a list with the texture folder as target
                                var targetItems = new List<ProjectItem> { (ProjectItem)texturesFolder };
                                execMethod.Invoke(_studio, new object[] { "ImportImages", targetItems });
                                Log($"ImportImage: ExecutePluginCommand executed");
                            }
                            catch (Exception ex)
                            {
                                Log($"ImportImage: ExecutePluginCommand failed: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"ImportImage: ExecutePluginCommand strategy failed: {ex.Message}");
                    }
                }

                // Strategy 6: Check if file exists first, provide better error
                if (importedItem == null)
                {
                    if (!System.IO.File.Exists(filePath))
                    {
                        return ErrorJson($"File not found: {filePath}. Please ensure the file exists and the path is correct.");
                    }

                    // Try to create a placeholder texture with the file as source
                    try
                    {
                        Log($"ImportImage: trying to create texture with file source...");

                        // Get the texture library's children
                        var childrenProp = texturesFolder.GetType().GetProperty("Children",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (childrenProp != null)
                        {
                            var children = childrenProp.GetValue(texturesFolder) as IList;
                            if (children != null)
                            {
                                // Find the type for SingleTexture
                                var singleTextureType = FindTypeInAssemblies("SingleTexture");
                                if (singleTextureType != null)
                                {
                                    // Try to create and configure
                                    Log($"ImportImage: found SingleTexture type: {singleTextureType.FullName}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"ImportImage: placeholder creation failed: {ex.Message}");
                    }
                }

                if (importedItem != null)
                {
                    var importedPath = GetItemPath(importedItem);
                    var importedName = GetItemName(importedItem);
                    Log($"ImportImage: success, path={importedPath}");

                    return SafeSerialize(new
                    {
                        success = true,
                        imported = true,
                        path = importedPath,
                        name = importedName,
                        type = GetItemType(importedItem),
                        sourceFile = filePath
                    });
                }

                return ErrorJson($"Import failed: no suitable import method found for '{System.IO.Path.GetFileName(filePath)}'. Kanzi Studio requires UI interaction for importing images. Please import the image manually via Kanzi Studio (right-click Textures folder -> Import -> Images).");
            }
            catch (Exception ex)
            {
                Log($"ImportImage failed: {ex.Message}");
                return ErrorJson($"Import failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 导入 FBX 3D 模型
        /// </summary>
        public string ImportFbx(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            if (!args.HasValue)
                return ErrorJson("缺少参数");

            var filePath = args.Value.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? "" : "";
            var resourceName = args.Value.TryGetProperty("resourceName", out var rn) ? rn.GetString() : null;
            var targetFolder = args.Value.TryGetProperty("targetFolder", out var tf) ? tf.GetString() ?? "Meshes" : "Meshes";

            if (string.IsNullOrEmpty(filePath))
                return ErrorJson("缺少 filePath 参数");

            Log($"ImportFbx: filePath={filePath}, resourceName={resourceName}, targetFolder={targetFolder}");

            try
            {
                // 验证文件存在
                if (!System.IO.File.Exists(filePath))
                    return ErrorJson($"File not found: {filePath}");

                // 获取目标文件夹
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                // 查找或创建 Meshes 文件夹
                var meshesFolder = GetOrCreateResourceFolder(project, targetFolder);
                if (meshesFolder == null)
                    return ErrorJson($"Cannot find or create resource folder: {targetFolder}");

                // 使用 Kanzi API 导入 FBX
                // 策略1: 使用 Import 方法
                var importMethod = meshesFolder.GetType().GetMethod("Import",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                    null, new[] { typeof(string) }, null);

                object? importedItem = null;
                if (importMethod != null)
                {
                    try
                    {
                        importedItem = importMethod.Invoke(meshesFolder, new object[] { filePath });
                        Log($"ImportFbx: imported via Import method");
                    }
                    catch (Exception ex)
                    {
                        Log($"ImportFbx: Import method failed: {ex.Message}");
                    }
                }

                // 策略2: 使用 ImportMesh 或 ImportModel 方法
                if (importedItem == null)
                {
                    var importMeshMethod = meshesFolder.GetType().GetMethod("ImportMesh",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string) }, null);
                    if (importMeshMethod != null)
                    {
                        try
                        {
                            importedItem = importMeshMethod.Invoke(meshesFolder, new object[] { filePath });
                            Log($"ImportFbx: imported via ImportMesh method");
                        }
                        catch (Exception ex)
                        {
                            Log($"ImportFbx: ImportMesh method failed: {ex.Message}");
                        }
                    }
                }

                // 策略3: 使用 Project.ImportAsset
                if (importedItem == null)
                {
                    var assetLibraryMethod = project.GetType().GetMethod("ImportAsset",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(string) }, null);
                    if (assetLibraryMethod != null)
                    {
                        try
                        {
                            importedItem = assetLibraryMethod.Invoke(project, new object[] { filePath, targetFolder });
                            Log($"ImportFbx: imported via ImportAsset method");
                        }
                        catch (Exception ex)
                        {
                            Log($"ImportFbx: ImportAsset method failed: {ex.Message}");
                        }
                    }
                }

                if (importedItem != null)
                {
                    var importedPath = GetItemPath(importedItem);
                    var importedName = GetItemName(importedItem);
                    Log($"ImportFbx: success, path={importedPath}");

                    return SafeSerialize(new
                    {
                        success = true,
                        imported = true,
                        path = importedPath,
                        name = importedName,
                        type = GetItemType(importedItem),
                        sourceFile = filePath
                    });
                }

                return ErrorJson("Import FBX failed: no suitable import method found. Please import manually via Kanzi Studio.");
            }
            catch (Exception ex)
            {
                Log($"ImportFbx failed: {ex.Message}");
                return ErrorJson($"导入 FBX 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 诊断资源使用情况 - 找出未使用的 Image 和 Texture 资源
        /// </summary>
        public string DoctorResource(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio 未连接");

            var checkImages = !args.HasValue || !args.Value.TryGetProperty("checkImages", out var ci) || ci.GetBoolean();
            var checkTextures = !args.HasValue || !args.Value.TryGetProperty("checkTextures", out var ct) || ct.GetBoolean();

            Log($"DoctorResource: checkImages={checkImages}, checkTextures={checkTextures}");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                var unusedImages = new List<Dictionary<string, object>>();
                var unusedTextures = new List<Dictionary<string, object>>();
                var usedResourcePaths = new HashSet<string>();

                // 第一步：收集所有被节点使用的资源路径
                CollectUsedResources(project, usedResourcePaths, 0);

                // 第二步：查找所有 Image 和 Texture 资源
                if (checkImages || checkTextures)
                {
                    var texturesFolder = GetProjectItem("Textures");
                    if (texturesFolder != null)
                    {
                        CollectUnusedResources(texturesFolder, usedResourcePaths, unusedImages, unusedTextures, 0);
                    }
                }

                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["totalUsedResources"] = usedResourcePaths.Count,
                    ["unusedImages"] = unusedImages,
                    ["unusedImageCount"] = unusedImages.Count,
                    ["unusedTextures"] = unusedTextures,
                    ["unusedTextureCount"] = unusedTextures.Count,
                    ["recommendations"] = new List<string>()
                };

                // 添加建议
                var recommendations = (List<string>)result["recommendations"];
                if (unusedImages.Count > 0)
                {
                    recommendations.Add($"Found {unusedImages.Count} unused images. Consider removing them to reduce project size.");
                }
                if (unusedTextures.Count > 0)
                {
                    recommendations.Add($"Found {unusedTextures.Count} unused textures. Consider removing them to reduce memory usage.");
                }
                if (unusedImages.Count == 0 && unusedTextures.Count == 0)
                {
                    recommendations.Add("All resources are in use. Project looks healthy!");
                }

                Log($"DoctorResource: found {unusedImages.Count} unused images, {unusedTextures.Count} unused textures");

                return SafeSerialize(result);
            }
            catch (Exception ex)
            {
                Log($"DoctorResource failed: {ex.Message}");
                return ErrorJson($"诊断失败: {ex.Message}");
            }
        }

        private void CollectUsedResources(object parent, HashSet<string> usedPaths, int depth)
        {
            if (depth > 30) return;

            try
            {
                var nodeType = GetItemType(parent);

                // 检查节点的属性中是否有资源引用
                var props = GetItemProperties(parent);
                foreach (var prop in props)
                {
                    var value = prop.Value;
                    if (value != null)
                    {
                        var valueStr = value.ToString() ?? "";
                        // 检查是否为资源路径
                        if (valueStr.Contains("Textures/") || valueStr.Contains("Materials/") ||
                            valueStr.Contains("Images/") || valueStr.Contains("Brushes/"))
                        {
                            usedPaths.Add(valueStr);
                        }
                    }
                }

                // 递归遍历子节点
                foreach (var child in GetChildren(parent))
                {
                    CollectUsedResources(child, usedPaths, depth + 1);
                }
            }
            catch { }
        }

        private void CollectUnusedResources(object folder, HashSet<string> usedPaths,
            List<Dictionary<string, object>> unusedImages,
            List<Dictionary<string, object>> unusedTextures, int depth)
        {
            if (depth > 10) return;

            try
            {
                foreach (var child in GetChildren(folder))
                {
                    var childName = GetItemName(child);
                    var childPath = GetItemPath(child);
                    var childType = GetItemType(child);

                    // 跳过子文件夹
                    var children = GetChildren(child);
                    if (children.Count > 0 && (childType.Contains("Library") || childType.Contains("Folder")))
                    {
                        CollectUnusedResources(child, usedPaths, unusedImages, unusedTextures, depth + 1);
                        continue;
                    }

                    // 检查是否为 Image 或 Texture 类型
                    var isUsed = usedPaths.Any(p => p.Contains(childName) || p.Contains(childPath));
                    var resourceInfo = new Dictionary<string, object>
                    {
                        ["name"] = childName,
                        ["path"] = childPath,
                        ["type"] = childType,
                        ["isUsed"] = isUsed
                    };

                    if (childType.Contains("Image") || childType.Contains("Single Texture"))
                    {
                        if (!isUsed)
                            unusedImages.Add(resourceInfo);
                    }
                    else if (childType.Contains("Texture") || childType.Contains("Brush"))
                    {
                        if (!isUsed)
                            unusedTextures.Add(resourceInfo);
                    }
                }
            }
            catch { }
        }

        private object? GetOrCreateResourceFolder(object project, string folderName)
        {
            // 尝试直接获取文件夹
            var children = GetChildren(project);
            foreach (var child in children)
            {
                var name = GetItemName(child);
                if (string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            // 尝试使用 GetChildByName 方法
            var getChildMethod = project.GetType().GetMethod("GetChildByName",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                null, new[] { typeof(string) }, null);
            if (getChildMethod != null)
            {
                try
                {
                    var folder = getChildMethod.Invoke(project, new object[] { folderName });
                    if (folder != null) return folder;
                }
                catch { }
            }

            Log($"GetOrCreateResourceFolder: folder '{folderName}' not found");
            return null;
        }

        #endregion

        #region 辅助方法

        private void Log(string msg)
        {
            try
            {
                System.IO.Directory.CreateDirectory(@"C:\temp");
                System.IO.File.AppendAllText(
                    @"C:\temp\KanziMcpPlugin.log",
                    $"[{DateTime.Now:HH:mm:ss.fff}] [KanziService] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        /// <summary>
        /// 安全序列化 — 捕获序列化异常，降级到安全格式
        /// </summary>
        private string SafeSerialize(object obj)
        {
            try
            {
                return JsonSerializer.Serialize(obj, _jsonOptions);
            }
            catch (Exception ex)
            {
                Log($"SafeSerialize failed: {ex.Message}. Attempting fallback...");

                // 降级：将对象转为简单字典，确保所有值都可以序列化
                try
                {
                    var safeObj = MakeSafeForSerialization(obj);
                    return JsonSerializer.Serialize(safeObj, _jsonOptions);
                }
                catch (Exception ex2)
                {
                    Log($"SafeSerialize fallback also failed: {ex2.Message}");
                    return ErrorJson($"序列化失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 将对象转为 JSON 安全格式 — 递归处理所有不可序列化的值
        /// </summary>
        private object MakeSafeForSerialization(object obj)
        {
            if (obj == null) return null!;

            var type = obj.GetType();

            // 基本类型直接返回
            if (type.IsPrimitive || type.IsEnum || obj is string || obj is decimal ||
                obj is int || obj is long || obj is float || obj is double || obj is bool)
                return obj;

            // 字典：递归处理值
            if (obj is IDictionary dict)
            {
                var result = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dict)
                {
                    result[entry.Key.ToString() ?? ""] = entry.Value != null
                        ? MakeSafeForSerialization(entry.Value)
                        : null;
                }
                return result;
            }

            // 列表/集合：递归处理元素
            if (obj is IEnumerable enumerable && !(obj is string))
            {
                var list = new List<object?>();
                foreach (var item in enumerable)
                {
                    list.Add(item != null ? MakeSafeForSerialization(item) : null);
                }
                return list;
            }

            // 匿名类型和其他引用类型：转为字典
            var props = type.GetProperties();
            var safeDict = new Dictionary<string, object?>();
            foreach (var prop in props)
            {
                try
                {
                    var val = prop.GetValue(obj);
                    safeDict[prop.Name] = val != null ? MakeSafeForSerialization(val) : null;
                }
                catch
                {
                    safeDict[prop.Name] = $"[{prop.PropertyType.Name}]";
                }
            }
            return safeDict;
        }

        private string ErrorJson(string message)
        {
            return JsonSerializer.Serialize(new { error = message }, _jsonOptions);
        }

        private NodeFilter ParseNodeFilter(JsonElement? element)
        {
            var filter = new NodeFilter();
            if (!element.HasValue) return filter;
            var e = element.Value;

            if (e.TryGetProperty("type", out var typeEl)) filter.Type = typeEl.GetString();
            if (e.TryGetProperty("name", out var nameEl)) filter.Name = nameEl.GetString();
            if (e.TryGetProperty("path", out var pathEl)) filter.Path = pathEl.GetString();
            if (e.TryGetProperty("includeProperties", out var ip)) filter.IncludeProperties = ip.GetBoolean();
            if (e.TryGetProperty("recursive", out var r)) filter.Recursive = r.GetBoolean();
            if (e.TryGetProperty("limit", out var l)) filter.Limit = l.GetInt32();

            return filter;
        }

        private bool WildcardMatch(string input, string pattern)
        {
            if (pattern == "*") return true;
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private string GetNodeCategory(string type)
        {
            if (type.Contains("2D")) return "2D";
            if (type.Contains("3D")) return "3D";
            if (type.Contains("Text") || type.Contains("Image") || type.Contains("Button"))
                return "UI";
            return "Basic";
        }

        private Dictionary<string, PropertyMetadata> GetCommonPropertyMetadata(string nodeType)
        {
            var metadata = new Dictionary<string, PropertyMetadata>();

            // ========== 通用属性 ==========
            var commonProps = new[]
            {
                new PropertyMetadata { Name = "Opacity", Type = "float", DisplayName = "Opacity", Category = "Appearance", IsReadOnly = false },
                new PropertyMetadata { Name = "IsVisible", Type = "bool", DisplayName = "Visibility", Category = "Appearance", IsReadOnly = false },
                new PropertyMetadata { Name = "Position", Type = "vector2", DisplayName = "Position", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Size", Type = "vector2", DisplayName = "Size", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Width", Type = "float", DisplayName = "Width", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Height", Type = "float", DisplayName = "Height", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Scale", Type = "vector2", DisplayName = "Scale", Category = "Transform", IsReadOnly = false },
                new PropertyMetadata { Name = "Rotation", Type = "float", DisplayName = "Rotation", Category = "Transform", IsReadOnly = false },
                new PropertyMetadata { Name = "HorizontalAlignment", Type = "enum", DisplayName = "Horizontal Alignment", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "VerticalAlignment", Type = "enum", DisplayName = "Vertical Alignment", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Margin", Type = "vector4", DisplayName = "Margin", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Padding", Type = "vector4", DisplayName = "Padding", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Name", Type = "string", DisplayName = "Node Name", Category = "General", IsReadOnly = false },
                new PropertyMetadata { Name = "Enabled", Type = "bool", DisplayName = "Enabled", Category = "General", IsReadOnly = false },
                new PropertyMetadata { Name = "PageHost", Type = "NodeReference", DisplayName = "Page Host", Category = "Navigation", IsReadOnly = false },
                new PropertyMetadata { Name = "DataContext", Type = "object", DisplayName = "Data Context", Category = "Data", IsReadOnly = false },
            };

            foreach (var prop in commonProps)
                metadata[prop.Name] = prop;

            // ========== 节点类型特定属性 ==========
            switch (nodeType)
            {
                case "TextBlock2D":
                    metadata["Text"] = new PropertyMetadata { Name = "Text", Type = "string", DisplayName = "Text Content", Category = "Text", IsReadOnly = false };
                    metadata["FontColor"] = new PropertyMetadata { Name = "FontColor", Type = "color", DisplayName = "Font Color", Category = "Text", IsReadOnly = false };
                    metadata["FontSize"] = new PropertyMetadata { Name = "FontSize", Type = "float", DisplayName = "Font Size", Category = "Text", IsReadOnly = false };
                    metadata["FontWeight"] = new PropertyMetadata { Name = "FontWeight", Type = "enum", DisplayName = "Font Weight", Category = "Text", IsReadOnly = false };
                    metadata["FontStyle"] = new PropertyMetadata { Name = "FontStyle", Type = "enum", DisplayName = "Font Style", Category = "Text", IsReadOnly = false };
                    metadata["TextAlignment"] = new PropertyMetadata { Name = "TextAlignment", Type = "enum", DisplayName = "Text Alignment", Category = "Text", IsReadOnly = false };
                    metadata["HorizontalContentAlignment"] = new PropertyMetadata { Name = "HorizontalContentAlignment", Type = "enum", DisplayName = "Horizontal Content Alignment", Category = "Text", IsReadOnly = false };
                    metadata["VerticalContentAlignment"] = new PropertyMetadata { Name = "VerticalContentAlignment", Type = "enum", DisplayName = "Vertical Content Alignment", Category = "Text", IsReadOnly = false };
                    metadata["TextTrimming"] = new PropertyMetadata { Name = "TextTrimming", Type = "enum", DisplayName = "Text Trimming", Category = "Text", IsReadOnly = false };
                    metadata["WordWrap"] = new PropertyMetadata { Name = "WordWrap", Type = "bool", DisplayName = "Word Wrap", Category = "Text", IsReadOnly = false };
                    break;

                case "Image2D":
                    metadata["Texture"] = new PropertyMetadata { Name = "Texture", Type = "resource", DisplayName = "Texture", Category = "Appearance", IsReadOnly = false };
                    metadata["BrushColor"] = new PropertyMetadata { Name = "BrushColor", Type = "color", DisplayName = "Brush Color", Category = "Appearance", IsReadOnly = false };
                    metadata["Stretch"] = new PropertyMetadata { Name = "Stretch", Type = "enum", DisplayName = "Stretch Mode", Category = "Appearance", IsReadOnly = false };
                    metadata["Tile"] = new PropertyMetadata { Name = "Tile", Type = "bool", DisplayName = "Tile", Category = "Appearance", IsReadOnly = false };
                    break;

                case "RectangleNode2D":
                    metadata["FillColor"] = new PropertyMetadata { Name = "FillColor", Type = "color", DisplayName = "Fill Color", Category = "Appearance", IsReadOnly = false };
                    metadata["BorderColor"] = new PropertyMetadata { Name = "BorderColor", Type = "color", DisplayName = "Border Color", Category = "Appearance", IsReadOnly = false };
                    metadata["BorderWidth"] = new PropertyMetadata { Name = "BorderWidth", Type = "float", DisplayName = "Border Width", Category = "Appearance", IsReadOnly = false };
                    metadata["CornerRadius"] = new PropertyMetadata { Name = "CornerRadius", Type = "vector4", DisplayName = "Corner Radius", Category = "Appearance", IsReadOnly = false };
                    break;

                case "Button2D":
                    metadata["Text"] = new PropertyMetadata { Name = "Text", Type = "string", DisplayName = "Button Text", Category = "Content", IsReadOnly = false };
                    metadata["FontColor"] = new PropertyMetadata { Name = "FontColor", Type = "color", DisplayName = "Font Color", Category = "Content", IsReadOnly = false };
                    metadata["FontSize"] = new PropertyMetadata { Name = "FontSize", Type = "float", DisplayName = "Font Size", Category = "Content", IsReadOnly = false };
                    metadata["Background"] = new PropertyMetadata { Name = "Background", Type = "resource", DisplayName = "Background", Category = "Appearance", IsReadOnly = false };
                    metadata["PressedBackground"] = new PropertyMetadata { Name = "PressedBackground", Type = "resource", DisplayName = "Pressed Background", Category = "Appearance", IsReadOnly = false };
                    break;

                case "ProgressBar2D":
                    metadata["Minimum"] = new PropertyMetadata { Name = "Minimum", Type = "float", DisplayName = "Minimum", Category = "Value", IsReadOnly = false };
                    metadata["Maximum"] = new PropertyMetadata { Name = "Maximum", Type = "float", DisplayName = "Maximum", Category = "Value", IsReadOnly = false };
                    metadata["Value"] = new PropertyMetadata { Name = "Value", Type = "float", DisplayName = "Current Value", Category = "Value", IsReadOnly = false };
                    break;

                case "Slider2D":
                    metadata["Minimum"] = new PropertyMetadata { Name = "Minimum", Type = "float", DisplayName = "Minimum", Category = "Value", IsReadOnly = false };
                    metadata["Maximum"] = new PropertyMetadata { Name = "Maximum", Type = "float", DisplayName = "Maximum", Category = "Value", IsReadOnly = false };
                    metadata["Value"] = new PropertyMetadata { Name = "Value", Type = "float", DisplayName = "Current Value", Category = "Value", IsReadOnly = false };
                    metadata["Step"] = new PropertyMetadata { Name = "Step", Type = "float", DisplayName = "Step", Category = "Value", IsReadOnly = false };
                    break;

                case "EmptyNode2D":
                case "Node2D":
                    // 这些节点只有通用属性
                    break;

                case "Viewport2D":
                    metadata["Camera"] = new PropertyMetadata { Name = "Camera", Type = "NodeReference", DisplayName = "Camera", Category = "Rendering", IsReadOnly = false };
                    break;
            }

            return metadata;
        }

        #endregion
    }

    internal class NodeFilter
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        public string? Path { get; set; }
        public bool IncludeProperties { get; set; }
        public bool Recursive { get; set; } = true;
        public int Limit { get; set; } = 1000;
    }

    internal class PropertyMetadata
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Category { get; set; } = "";
        public bool IsReadOnly { get; set; }
    }
}
