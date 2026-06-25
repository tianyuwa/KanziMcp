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

                // Convert PluginInterface types to internal types for API access
                var internalParent = GetInternalProjectItem(parentItem);
                var internalProject = GetInternalProjectItem(project);
                var normalizedType = NormalizeNodeTypeName(nodeType);
                Log($"CreateNode: parentItem={parentItem.GetType().Name}, internalParent={internalParent?.GetType().Name}, project={project.GetType().Name}, internalProject={internalProject?.GetType().Name}");

                // ═══════════════════════════════════════════════════════════
                // SDK 优先路径: Project.CreateProjectItem<T>（卡片 11 新增）
                // ═══════════════════════════════════════════════════════════
                object? newNode = null;
                var sdkProject = GetSdkProject();
                if (sdkProject != null && parentItem is ProjectItem sdkParent)
                {
                    var nodeTypeObj = FindNodeType(nodeType);
                    if (nodeTypeObj != null)
                    {
                        try
                        {
                            var createProjectItemMethod = typeof(Project).GetMethod("CreateProjectItem",
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            if (createProjectItemMethod != null && createProjectItemMethod.IsGenericMethodDefinition)
                            {
                                var genericMethod = createProjectItemMethod.MakeGenericMethod(nodeTypeObj);
                                var createName = nodeName ?? $"New{nodeType}";
                                newNode = genericMethod.Invoke(sdkProject, new object[] { createName, sdkParent });
                                if (newNode != null)
                                    Log($"CreateNode: SDK CreateProjectItem<{nodeTypeObj.Name}> succeeded for '{createName}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"CreateNode: SDK CreateProjectItem<{nodeType}> failed ({ex.Message}), falling back to reflection strategies");
                        }
                    }
                    else
                    {
                        Log($"CreateNode: FindNodeType('{nodeType}') returned null, falling back to reflection strategies");
                    }
                }

                if (newNode == null)
                    Log($"CreateNode: SDK path not available or failed, using reflection strategies");

                // ═══════════════════════════════════════════════════════════
                // 反射降级策略链（保留全部 8 个现有策略）
                // ═══════════════════════════════════════════════════════════

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

                // Strategy 2: NodeComponentTypeLibrary — on internal project type
                if (newNode == null)
                {
                    try
                    {
                        var libSource = internalProject ?? project;
                        var typeLibProp = libSource.GetType().GetProperty("NodeComponentTypeLibrary",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (typeLibProp != null)
                        {
                            var typeLib = typeLibProp.GetValue(libSource);
                            if (typeLib is IEnumerable libItems)
                            {
                                var allNames = new List<string>();
                                foreach (var item in libItems)
                                {
                                    var itemName = GetItemName(item);
                                    allNames.Add(itemName);
                                    if (string.Equals(itemName, nodeType, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(NormalizeNodeTypeName(itemName), normalizedType, StringComparison.OrdinalIgnoreCase))
                                    {
                                        Log($"CreateNode: found NodeComponentTypeLibrary item: {itemName}");
                                        var defaultInst = SafeGetProperty(item, "DefaultInstance");
                                        if (defaultInst != null)
                                        {
                                            var cloneMethod = defaultInst.GetType().GetMethod("CloneUnder",
                                                BindingFlags.Public | BindingFlags.Instance);
                                            if (cloneMethod != null)
                                            {
                                                try
                                                {
                                                    var pars = cloneMethod.GetParameters();
                                                    var cloneName = nodeName ?? $"New{nodeType}";
                                                    var cloneParent = internalParent ?? parentItem;
                                                    if (pars.Length >= 3)
                                                        newNode = cloneMethod.Invoke(defaultInst, new[] { cloneName, cloneParent, Enum.GetValues(pars[2].ParameterType).GetValue(0) });
                                                    else
                                                        newNode = cloneMethod.Invoke(defaultInst, new[] { cloneName, cloneParent });
                                                    if (newNode != null)
                                                        Log($"CreateNode: created via NodeComponentTypeLibrary CloneUnder");
                                                }
                                                catch (Exception ex) { Log($"CreateNode: NodeComponentTypeLibrary CloneUnder failed: {ex.Message}"); }
                                            }
                                        }
                                        break;
                                    }
                                }
                                if (newNode == null && allNames.Count > 0)
                                    Log($"CreateNode: NodeComponentTypeLibrary has {allNames.Count} items, none match '{nodeType}': {string.Join(", ", allNames.Take(20))}");
                            }
                            else
                            {
                                Log($"CreateNode: NodeComponentTypeLibrary value is not IEnumerable");
                            }
                        }
                        else
                        {
                            Log($"CreateNode: NodeComponentTypeLibrary property not found on {libSource.GetType().Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateNode: NodeComponentTypeLibrary approach failed: {ex.Message}");
                    }
                }

                // Strategy 2b (ComponentTypeLibrary) 已删除 — ApiDump L221 确认 SDK 覆盖。
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

                // Strategy 6 (Project.CreateNode) 已删除 — ApiDump 无匹配，SDK CreateProjectItem<T> 已覆盖。
                // Strategy 7: CloneUnder - Find a template node and clone it
                if (newNode == null)
                {
                    try
                    {
                        Log($"CreateNode: trying CloneUnder strategy...");

                        // First, find a template node of the requested type
                        string? templatePath = nodeType switch
                        {
                            "EmptyNode2D" or "Empty Node 2D" => "Templates/DefaultNode2D",
                            "Node2D" or "Node 2D" => "Templates/DefaultNode2D",
                            "TextBlock2D" or "Text Block 2D" => "Templates/DefaultTextBlock2D",
                            "RectangleNode2D" or "Rectangle Node 2D" => "Templates/DefaultRectangleNode2D",
                            "Image2D" or "Image 2D" => "Templates/DefaultImage2D",
                            _ => null
                        };

                        object? templateNode = null;
                        if (templatePath != null)
                        {
                            templateNode = GetProjectItem(templatePath);
                        }

                        // Prefer user Prefabs / parent subtree; avoid Isolation and MCP Test_* clones
                        if (templateNode == null)
                        {
                            try
                            {
                                var (bestTemplate, bestPath) = FindBestTemplateNode(project, parentPath, nodeType);
                                if (bestTemplate != null)
                                {
                                    templateNode = bestTemplate;
                                    Log($"CreateNode: found template node: {bestPath} (score-based selection)");
                                }
                                else
                                    Log($"CreateNode: no template found for type '{nodeType}' (aliases: {string.Join(", ", GetNodeTypeAliases(nodeType).Take(6))})");
                            }
                            catch (Exception ex)
                            {
                                Log($"CreateNode: template search failed: {ex.Message}");
                            }
                        }

                        // If we have a template node, try CloneUnder on internal type
                        if (templateNode != null)
                        {
                            var internalTemplate = GetInternalProjectItem(templateNode);
                            Log($"CreateNode: template={templateNode.GetType().Name}, internalTemplate={internalTemplate?.GetType().Name}");

                            if (internalTemplate == null)
                            {
                                Log($"CreateNode: GetInternalProjectItem returned null for template");
                            }
                            else
                            {
                                var cloneUnderMethod = internalTemplate.GetType().GetMethod("CloneUnder",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (cloneUnderMethod != null)
                                {
                                    try
                                    {
                                        var cloneMethodType = cloneUnderMethod.GetParameters()[2].ParameterType;
                                        var cloneMethodValues = Enum.GetValues(cloneMethodType);
                                        var defaultCloneMethod = cloneMethodValues.GetValue(0);
                                        var cloneParent = internalParent ?? parentItem;

                                        newNode = cloneUnderMethod.Invoke(internalTemplate,
                                            new[] { nodeName ?? $"New{nodeType}", cloneParent, defaultCloneMethod });
                                        if (newNode != null)
                                        {
                                            Log($"CreateNode: created via CloneUnder");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"CreateNode: CloneUnder invoke failed: {ex.Message}");
                                        if (ex.InnerException != null)
                                            Log($"CreateNode: CloneUnder InnerException: {ex.InnerException.Message}");
                                    }
                                }
                                else
                                {
                                    Log($"CreateNode: CloneUnder method not found on {internalTemplate.GetType().Name}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateNode: CloneUnder strategy failed: {ex.Message}");
                    }
                }

                // Strategy 8: ExecutePluginCommand on KanziStudio
                if (newNode == null && _studio != null)
                {
                    try
                    {
                        Log($"CreateNode: trying ExecutePluginCommand strategy...");

                        string? commandName = nodeType switch
                        {
                            "EmptyNode2D" or "Empty Node 2D" or "Node2D" or "Node 2D" or "2DNode" => "CreateEmptyNode2D",
                            "EmptyNode3D" or "Empty Node 3D" or "Node3D" or "Node 3D" or "3DNode" => "CreateEmptyNode3D",
                            "TextBlock2D" or "Text Block 2D" or "2DText" => "CreateTextBlock2D",
                            "RectangleNode2D" or "Rectangle Node 2D" => "CreateRectangleNode2D",
                            "Image2D" or "Image 2D" or "2DImage" => "CreateImage2D",
                            _ => null
                        };

                        if (commandName != null)
                        {
                            if (TryExecuteKanziPluginCommand(commandName, parentItem, nodeType, nodeName, out var cmdNode))
                            {
                                newNode = cmdNode;
                                Log($"CreateNode: created via KanziUIEnvironment plugin command: {commandName}");
                            }
                            else
                            {
                                // Build items list: parentItem IS PluginInterface.ProjectItem
                                var listType = typeof(List<>).MakeGenericType(parentItem.GetType());
                                var itemsList = (System.Collections.IList)Activator.CreateInstance(listType)!;
                                itemsList.Add(parentItem);

                                // Try _studio first (KanziStudio has ExecutePluginCommand)
                                foreach (var target in new[] { _studio, project, parentItem })
                                {
                                    if (target == null) continue;
                                    MethodInfo? targetExec = null;
                                    foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                                    {
                                        if (m.Name != "ExecutePluginCommand") continue;
                                        var p = m.GetParameters();
                                        if (p.Length == 2 && p[0].ParameterType == typeof(string))
                                        { targetExec = m; break; }
                                    }

                                    if (targetExec == null)
                                    {
                                        Log($"CreateNode: no ExecutePluginCommand(string, IEnumerable) on {target.GetType().Name}");
                                        continue;
                                    }

                                    try
                                    {
                                        Log($"CreateNode: ExecutePluginCommand on {target.GetType().Name} with {commandName}");
                                        targetExec.Invoke(target, new object[] { commandName, itemsList });
                                        Log($"CreateNode: ExecutePluginCommand executed: {commandName}");

                                        var childName = nodeName ?? $"New{nodeType}";
                                        var children = GetChildren(parentItem);
                                        newNode = children.FirstOrDefault(c =>
                                        {
                                            var n = GetItemName(c);
                                            return n == childName || n.Contains(nodeType) || n.Contains(normalizedType);
                                        });
                                        if (newNode != null) { Log($"CreateNode: found new node after ExecutePluginCommand: {GetItemName(newNode)}"); break; }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"CreateNode: ExecutePluginCommand on {target.GetType().Name} failed: {ex.Message}");
                                        if (ex.InnerException != null)
                                            Log($"CreateNode: ExecutePluginCommand InnerException: {ex.InnerException.Message}");
                                    }
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

                if (!IsCreatedNodeTypeCompatible(nodeType, newNode))
                {
                    var actualType = GetItemType(newNode);
                    var actualPath = GetItemPath(newNode);

                    // If wrapper / internal type is correct but TypeDisplayName differs
                    // (e.g. Kanzi reports "Image" for an Image2D node), treat as success.
                    // Only reject when the actual wrapper type is genuinely wrong.
                    var internalTypeName = newNode.GetType().Name;
                    var logicalType = MapDisplayNameToLogicalType(actualType, internalTypeName);
                    if (!string.Equals(actualType, logicalType, StringComparison.OrdinalIgnoreCase) &&
                        IsWrapperDimensionCompatible(nodeType, internalTypeName))
                    {
                        Log($"CreateNode: TypeDisplayName mismatch (expected '{nodeType}', got display '{actualType}', " +
                            $"internal type '{internalTypeName}') — node is correct, proceeding");
                    }
                    else
                    {
                        Log($"CreateNode: type mismatch — requested '{nodeType}', got '{actualType}' at {actualPath}");
                        return ErrorJson(
                            $"Created wrong node type: requested '{nodeType}' but got '{actualType}' at '{actualPath}'. " +
                            $"Check that the template type matches (e.g. Image2D vs Image3D, or Empty Node 2D vs Empty Node 3D).");
                    }
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
        /// 规范化节点类型名称 — "EmptyNode2D" vs "Empty Node 2D"
        /// </summary>
        private static string NormalizeNodeTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return typeName;
            return typeName.Replace(" ", "");
        }

        private static readonly Dictionary<string, string[]> NodeTypeAliasMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Empty Node 2D"] = new[] { "Empty Node 2D", "EmptyNode2D", "Node 2D", "Node2D", "2DNode" },
                ["Empty Node 3D"] = new[] { "Empty Node 3D", "EmptyNode3D", "Node 3D", "Node3D", "3DNode" },
                ["Text Block 2D"] = new[] { "Text Block 2D", "TextBlock2D", "2DText" },
                ["Text Block 3D"] = new[] { "Text Block 3D", "TextBlock3D", "3DText" },
                ["Image 2D"] = new[] { "Image 2D", "Image2D", "2DImage", "Image" },
                ["Image 3D"] = new[] { "Image 3D", "Image3D", "3DImage" },
                ["Rectangle Node 2D"] = new[] { "Rectangle Node 2D", "RectangleNode2D" },
            };

        private static IEnumerable<string> GetNodeTypeAliases(string nodeType)
        {
            yield return nodeType;
            if (NodeTypeAliasMap.TryGetValue(nodeType, out var aliases))
            {
                foreach (var alias in aliases)
                    yield return alias;
            }
            else
            {
                foreach (var entry in NodeTypeAliasMap)
                {
                    if (entry.Value.Any(a => string.Equals(a, nodeType, StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(NormalizeNodeTypeName(a), NormalizeNodeTypeName(nodeType),
                                                 StringComparison.OrdinalIgnoreCase)))
                    {
                        yield return entry.Key;
                        foreach (var alias in entry.Value)
                            yield return alias;
                    }
                }
            }
        }

        /// <summary>从类型名提取维度：2D / 3D，用于避免 EmptyNode2D 被当成 EmptyNode3D 模板。</summary>
        private static string? GetNodeDimension(string typeName)
        {
            var n = NormalizeNodeTypeName(typeName);
            if (n.Contains("3D")) return "3D";
            if (n.Contains("2D")) return "2D";
            return null;
        }

        /// <summary>请求维度与 PluginWrapper 是否一致（禁止 3D 请求匹配 *2D* wrapper）。</summary>
        private static bool IsWrapperDimensionCompatible(string requestedType, string wrapperName)
        {
            var reqDim = GetNodeDimension(requestedType);
            if (reqDim == null) return true;

            var w = NormalizeNodeTypeName(wrapperName);
            if (reqDim == "2D")
                return w.Contains("2D");
            // 3D：wrapper 不得含 2D（EmptyNode2DPluginWrapper 会被排除）
            return !w.Contains("2D");
        }

        /// <summary>
        /// Maps Kanzi Studio TypeDisplayName to MCP logical node type.
        /// Some Kanzi types use short display names (e.g. "Image" for Image2D nodes),
        /// which would fail a naive string comparison against the requested type.
        /// </summary>
        private static string MapDisplayNameToLogicalType(string displayName, string internalTypeName)
        {
            if (string.Equals(displayName, "Image", StringComparison.OrdinalIgnoreCase))
            {
                var dim = GetNodeDimension(internalTypeName);
                if (dim == "2D") return "Image2D";
                if (dim == "3D") return "Image3D";
            }
            return displayName;
        }

        private static string? GetExpectedWrapperHint(string nodeType)
        {
            var n = NormalizeNodeTypeName(nodeType);
            // 使用完整 PluginWrapper 名，避免 "EmptyNode" 同时命中 2D 与 3D
            if (n.Contains("EmptyNode") && n.Contains("3D")) return "EmptyNodePluginWrapper";
            if (n.Contains("EmptyNode") && n.Contains("2D")) return "EmptyNode2DPluginWrapper";
            if (n.Contains("TextBlock") && n.Contains("3D")) return "TextBlock3DPluginWrapper";
            if (n.Contains("TextBlock") && n.Contains("2D")) return "TextBlock2DPluginWrapper";
            if (n.Contains("Image") && n.Contains("3D")) return "Image3DPluginWrapper";
            if (n.Contains("Image") && n.Contains("2D")) return "Image2DPluginWrapper";
            if (n.Contains("RectangleNode") || (n.Contains("Rectangle") && n.Contains("2D")))
                return "RectangleNode2DPluginWrapper";
            if (n is "Node3D" or "3DNode" || (n.Contains("Node") && n.Contains("3D") && !n.Contains("2D")))
                return "EmptyNodePluginWrapper";
            return null;
        }

        private static bool WrapperMatchesHint(string wrapperName, string wrapperHint)
        {
            if (wrapperHint.EndsWith("PluginWrapper", StringComparison.Ordinal))
                return wrapperName.Equals(wrapperHint, StringComparison.OrdinalIgnoreCase);
            return wrapperName.Contains(wrapperHint, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesPluginWrapperType(object node, string requestedType)
        {
            var wrapperName = node.GetType().Name;
            if (!wrapperName.EndsWith("PluginWrapper", StringComparison.Ordinal))
                return false;

            if (!IsWrapperDimensionCompatible(requestedType, wrapperName))
                return false;

            var coreType = wrapperName.Substring(0, wrapperName.Length - "PluginWrapper".Length);
            if (string.Equals(NormalizeNodeTypeName(coreType), NormalizeNodeTypeName(requestedType),
                    StringComparison.OrdinalIgnoreCase))
                return true;

            // Empty Node 3D：内部类型名为 EmptyNode（无 3D 后缀）
            var n = NormalizeNodeTypeName(requestedType);
            if (n.Contains("EmptyNode") && n.Contains("3D") &&
                coreType.Equals("EmptyNode", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static bool MatchesRequestedNodeType(string itemType, object node, string requestedType)
        {
            if (!IsWrapperDimensionCompatible(requestedType, node.GetType().Name))
                return false;

            foreach (var alias in GetNodeTypeAliases(requestedType))
            {
                if (string.Equals(itemType, alias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeNodeTypeName(itemType), NormalizeNodeTypeName(alias),
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Map Kanzi display name → logical type and re-check against aliases.
            // e.g. TypeDisplayName "Image" → logical type "Image2D" for Image2D nodes.
            var logicalType = MapDisplayNameToLogicalType(itemType, node.GetType().Name);
            if (!string.Equals(itemType, logicalType, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var alias in GetNodeTypeAliases(requestedType))
                {
                    if (string.Equals(logicalType, alias, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(NormalizeNodeTypeName(logicalType), NormalizeNodeTypeName(alias),
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            var wrapperHint = GetExpectedWrapperHint(requestedType);
            if (!string.IsNullOrEmpty(wrapperHint))
            {
                var wrapperName = node.GetType().Name;
                if (WrapperMatchesHint(wrapperName, wrapperHint))
                    return true;
            }

            if (MatchesPluginWrapperType(node, requestedType))
                return true;

            return false;
        }

        /// <summary>
        /// Validates that the created node type matches the requested type.
        /// Uses wrapper type + dimension + display-name mapping, not a naive
        /// TypeDisplayName string comparison, because Kanzi may report
        /// "Image" as the display name for an Image2D node.
        /// </summary>
        private bool IsCreatedNodeTypeCompatible(string requestedType, object createdNode)
        {
            // Primary validation: display name against aliases + wrapper/dimension checks
            if (MatchesRequestedNodeType(GetItemType(createdNode), createdNode, requestedType))
                return true;

            // Fallback: map display name → logical type (e.g. "Image" → "Image2D")
            // and re-validate. This handles nodes where CloneUnder succeeded with the
            // correct wrapper but Kanzi reports a short display name.
            var displayType = GetItemType(createdNode);
            var logicalType = MapDisplayNameToLogicalType(displayType, createdNode.GetType().Name);
            if (!string.Equals(displayType, logicalType, StringComparison.OrdinalIgnoreCase))
                return MatchesRequestedNodeType(logicalType, createdNode, requestedType);

            return false;
        }

        private static bool IsExcludedTemplatePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            return path.Contains("<Isolation", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("Styles/", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("Material Types/", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("Object Sources/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLowQualityTemplateName(string nodeName)
        {
            if (string.IsNullOrEmpty(nodeName)) return true;
            return nodeName.StartsWith("Test_", StringComparison.OrdinalIgnoreCase);
        }

        private int ScoreTemplateCandidate(string path, object node, string requestedType, string? parentPath)
        {
            var score = 0;
            var nodeName = GetItemName(node);

            if (path.StartsWith("Prefabs/", StringComparison.OrdinalIgnoreCase))
                score += 200;
            if (!string.IsNullOrEmpty(parentPath))
            {
                if (path.Equals(parentPath, StringComparison.OrdinalIgnoreCase))
                    score += 150;
                else if (path.StartsWith(parentPath + "/", StringComparison.OrdinalIgnoreCase))
                    score += 120;
            }
            if (path.StartsWith("Screens/", StringComparison.OrdinalIgnoreCase))
                score += 40;

            var wrapperHint = GetExpectedWrapperHint(requestedType);
            if (!string.IsNullOrEmpty(wrapperHint) &&
                WrapperMatchesHint(node.GetType().Name, wrapperHint))
                score += 80;

            if (!IsWrapperDimensionCompatible(requestedType, node.GetType().Name))
                score -= 2000;

            if (IsLowQualityTemplateName(nodeName))
                score -= 300;
            if (IsExcludedTemplatePath(path))
                score -= 1000;

            return score;
        }

        private (object? node, string? path) FindBestTemplateNode(object project, string? parentPath, string nodeType)
        {
            (object node, string path, int score)? best = null;

            void Consider(string path, object node, string type)
            {
                if (!MatchesRequestedNodeType(type, node, nodeType))
                    return;
                if (IsExcludedTemplatePath(path))
                    return;

                var score = ScoreTemplateCandidate(path, node, nodeType, parentPath);
                if (best == null || score > best.Value.score)
                    best = (node, path, score);
            }

            void Walk(object parent, string currentPath, int depth)
            {
                if (depth > 25) return;
                foreach (var child in GetChildren(parent))
                {
                    if (child == null) continue;
                    var name = GetItemName(child);
                    if (string.IsNullOrEmpty(name)) continue;
                    var path = string.IsNullOrEmpty(currentPath) ? name : $"{currentPath}/{name}";
                    var type = GetItemType(child);
                    Consider(path, child, type);
                    Walk(child, path, depth + 1);
                }
            }

            Walk(project, "", 0);
            return best.HasValue ? (best.Value.node, best.Value.path) : (null, null);
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
    }
}
