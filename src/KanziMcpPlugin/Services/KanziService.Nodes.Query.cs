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
                                // SDK 优先：DisplayName 可从 ProjectItem.TypeDisplayName 读取
                                var displayName = (item is ProjectItem pi ? pi.TypeDisplayName : null)
                                    ?? SafeGetProperty(item, "DisplayName") as string
                                    ?? GetItemName(item);
                                // Category 非标准 SDK 属性，保留反射兜底
                                var category = SafeGetProperty(item, "Category") as string ?? "General";

                                types.Add(new Dictionary<string, object?>
                                {
                                    ["type"] = GetItemName(item),
                                    ["displayName"] = displayName,
                                    ["category"] = category
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

                var bindings = GetBindings(item);

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
                            // SDK 优先：通过 PropertyContainer.Get 读取
                            string? textValue = null;
                            if (child is PropertyContainer pc)
                            {
                                try { textValue = pc.Get("Text") as string; }
                                catch { }
                            }
                            textValue ??= SafeGetProperty(child, "Text") as string;

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

    }
}
