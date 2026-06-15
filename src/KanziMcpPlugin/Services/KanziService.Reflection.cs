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
    public partial class KanziService
    {
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
            if (string.IsNullOrEmpty(path))
                return path;

            // 处理 kzb://projectName/path 格式（Kanzi URI）
            if (path.StartsWith("kzb://"))
            {
                var withoutScheme = path.Substring(6); // Remove "kzb://"
                var slashIdx = withoutScheme.IndexOf('/');
                if (slashIdx >= 0)
                {
                    // kzb://projectName/rest/of/path -> rest/of/path
                    var afterProject = withoutScheme.Substring(slashIdx + 1);
                    return afterProject;
                }
                // kzb://projectName (no trailing path) -> empty (root)
                return "";
            }

            if (string.IsNullOrEmpty(_projectName))
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
                // 策略3: Library 容器用 Items / ProjectItems（TextureLibrary 等）
                foreach (var propName in new[] { "Items", "ProjectItems" })
                {
                    var itemsProp = type.GetProperty(propName, flags);
                    if (itemsProp == null) continue;
                    try
                    {
                        if (itemsProp.GetValue(projectItem) is IEnumerable items)
                        {
                            foreach (var item in items)
                                result.Add(item);
                            if (result.Count > 0) return result;
                        }
                    }
                    catch (Exception ex) { Log($"GetChildren: {propName} failed: {ex.Message}"); }
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
    }
}
