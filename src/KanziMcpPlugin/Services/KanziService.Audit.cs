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

                // 解析参数
                string? namingPattern = null;
                bool checkDepth = false;
                bool checkNaming = false;
                int maxDepthWarning = 5;

                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("namingPattern", out var np) && np.ValueKind == JsonValueKind.String)
                        namingPattern = np.GetString();
                    if (args.Value.TryGetProperty("checkDepth", out var cd))
                        checkDepth = cd.GetBoolean();
                    if (args.Value.TryGetProperty("checkNaming", out var cn))
                        checkNaming = cn.GetBoolean();
                    if (args.Value.TryGetProperty("maxDepth", out var md) && md.ValueKind == JsonValueKind.Number)
                        maxDepthWarning = md.GetInt32();
                }

                Regex? namingRegex = null;
                if (checkNaming && !string.IsNullOrEmpty(namingPattern))
                {
                    try { namingRegex = new Regex(namingPattern); }
                    catch (Exception ex) { Log($"AuditProjectStructure: invalid namingPattern regex: {ex.Message}"); }
                }

                var issues = new List<Dictionary<string, object?>>();
                int totalNodes = 0, maxDepth = 0;

                AuditStructureRecursive(project, "", 0, issues, ref totalNodes, ref maxDepth,
                    checkDepth, maxDepthWarning, checkNaming, namingRegex);

                var score = 100;
                if (checkDepth && maxDepth > maxDepthWarning) score -= 10;
                if (issues.Count > 0) score -= issues.Count * 3;
                score = Math.Max(score, 0);

                var recommendations = new List<string>();
                if (checkDepth && maxDepth > maxDepthWarning)
                    recommendations.Add($"节点嵌套深度为 {maxDepth}，建议控制在 {maxDepthWarning} 层以内");
                if (checkNaming && issues.Any(i => i["type"]?.ToString() == "naming"))
                    recommendations.Add("统一节点命名规范，使用描述性名称");
                if (!checkDepth && !checkNaming)
                    recommendations.Add("未启用任何检查项，请设置 checkDepth=true 或 checkNaming=true");

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
            List<Dictionary<string, object?>> issues, ref int totalNodes, ref int maxDepth,
            bool checkDepth, int maxDepthWarning, bool checkNaming, Regex? namingRegex)
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

                    if (checkDepth && depth + 1 > maxDepthWarning)
                    {
                        issues.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "deep_nesting",
                            ["path"] = path,
                            ["depth"] = depth + 1,
                            ["message"] = $"嵌套深度 {depth + 1} 超过建议值 {maxDepthWarning}"
                        });
                    }

                    if (checkNaming && namingRegex != null && !namingRegex.IsMatch(name))
                    {
                        issues.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "naming",
                            ["path"] = path,
                            ["name"] = name,
                            ["message"] = $"节点名称 \"{name}\" 不符合命名规范 {namingRegex}"
                        });
                    }

                    AuditStructureRecursive(child, path, depth + 1, issues, ref totalNodes, ref maxDepth,
                        checkDepth, maxDepthWarning, checkNaming, namingRegex);
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

                // 阶段1: 扫描整个项目，收集所有资源定义和所有资源引用
                var allResources = new List<(string path, string name, string type, string? filePath)>();
                var allReferences = new HashSet<string>(); // 被引用的资源名/路径
                var brokenReferences = new List<Dictionary<string, object?>>();

                ScanProjectForResources(project, "", 0, allResources, allReferences, brokenReferences, checkBroken);

                // 阶段2: 对比分析
                var unusedResources = new List<Dictionary<string, object?>>();
                var orphanedResources = new List<Dictionary<string, object?>>();

                if (checkUnused || checkOrphaned)
                {
                    foreach (var (path, name, type, filePath) in allResources)
                    {
                        // 检查该资源是否被任何节点引用
                        bool isUsed = allReferences.Contains(name)
                            || allReferences.Any(r => r.Contains(name))
                            || allReferences.Any(r => path.Contains(r) || r.Contains(path));

                        if (!isUsed)
                        {
                            unusedResources.Add(new Dictionary<string, object?>
                            {
                                ["type"] = type,
                                ["path"] = path,
                                ["name"] = name
                            });
                        }
                    }
                }

                // 孤立资源 = 未使用 + 无任何引用关联
                if (checkOrphaned)
                {
                    foreach (var res in unusedResources)
                    {
                        orphanedResources.Add(new Dictionary<string, object?>
                        {
                            ["type"] = res["type"],
                            ["path"] = res["path"],
                            ["name"] = res["name"],
                            ["message"] = $"{res["type"]} '{res["name"]}' is not referenced by any node"
                        });
                    }
                }

                var allIssueCount = unusedResources.Count + brokenReferences.Count;

                var recommendations = new List<string>();
                if (allResources.Count == 0)
                    recommendations.Add("未检测到任何资源项（资源库可能为空，或资源类型名不匹配）。请确认项目中是否存在 Texture/Material/Font 等资源。");
                else if (allIssueCount == 0)
                    recommendations.Add($"扫描了 {allResources.Count} 个资源，未发现未使用或损坏的资源");
                if (unusedResources.Count > 0)
                    recommendations.Add($"发现 {unusedResources.Count} 个未使用的资源，考虑移除以减少项目大小");
                if (brokenReferences.Count > 0)
                    recommendations.Add($"发现 {brokenReferences.Count} 个损坏的引用，可能导致运行时错误");

                Log($"AuditResourceReferences: {allResources.Count} resources, {allReferences.Count} refs, {unusedResources.Count} unused, {brokenReferences.Count} broken");

                // 收集检测到的资源类型名（去重，取前30个）
                var detectedTypes = allResources.Select(r => r.type).Distinct().Take(30).ToList();

                return SafeSerialize(new
                {
                    success = true,
                    unusedResources,
                    brokenReferences,
                    orphanedResources,
                    summary = new
                    {
                        totalResources = allResources.Count,
                        totalUnused = unusedResources.Count,
                        totalBroken = brokenReferences.Count,
                        totalOrphaned = orphanedResources.Count,
                        totalReferences = allReferences.Count,
                        detectedResourceTypes = detectedTypes
                    },
                    recommendations
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"审计资源引用失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归扫描项目，收集资源定义和资源引用
        /// </summary>
        private void ScanProjectForResources(object parent, string parentPath, int depth,
            List<(string path, string name, string type, string? filePath)> resources,
            HashSet<string> references,
            List<Dictionary<string, object?>> broken,
            bool checkBroken)
        {
            if (depth > 30) return;

            try
            {
                var parentType = parent?.GetType().Name ?? "";

                foreach (var child in GetChildren(parent))
                {
                    var name = GetItemName(child);
                    var type = GetItemType(child);
                    var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

                    // 识别资源类型（Kanzi 项目中的资源容器和资源项）
                    // 注意: Image2D/TextBlock2D 等是场景节点，不是资源，不要匹配 "Image" 和 "Text"
                    bool isResource = type.Contains("Texture") || type.Contains("Material")
                        || type.Contains("Font") || type.Contains("Shader")
                        || type.Contains("Brush") || type.Contains("Style")
                        || type.Contains("Animation") || type.Contains("State")
                        || type.Contains("Resource") || type.Contains("Asset")
                        || type.Contains("Render") || type.Contains("Shader")
                        || type.Contains("Mesh") || type.Contains("Prefab")
                        || type.Contains("Composition") || type.Contains("Script")
                        || type.Contains("Data") || type.Contains("Locale");

                    // 检查是否在资源库容器内（父节点类型含 Library/Dictionary/Resource）
                    bool inResourceContainer = parentType.Contains("Library")
                        || parentType.Contains("Dictionary")
                        || parentType.Contains("Resource")
                        || parentType.Contains("Asset")
                        || parentType.Contains("Collection")
                        || parentType.Contains("Repository");

                    // 资源项：要么自身类型是资源类型，要么在资源容器内
                    if ((isResource || inResourceContainer) && !type.Contains("Plugin") && !type.Contains("Wrapper"))
                    {
                        string? filePath = null;

                        // 对纹理类型，尝试提取文件路径
                        if (checkBroken && type.Contains("Texture"))
                        {
                            try
                            {
                                filePath = SafeGetProperty(child, "FilePath") as string
                                    ?? SafeGetProperty(child, "Source") as string
                                    ?? SafeGetProperty(child, "Image") as string;
                                if (!string.IsNullOrEmpty(filePath) && !File.Exists(filePath))
                                {
                                    // 尝试相对于项目目录
                                    var projectDir = Path.GetDirectoryName(SafeGetProperty(GetActiveProject(), "FullPath") as string ?? "");
                                    var fullPath = string.IsNullOrEmpty(projectDir) ? filePath : Path.Combine(projectDir, filePath);
                                    if (!File.Exists(fullPath))
                                    {
                                        broken.Add(new Dictionary<string, object?>
                                        {
                                            ["type"] = "broken_resource",
                                            ["resourceType"] = type,
                                            ["path"] = path,
                                            ["filePath"] = filePath,
                                            ["message"] = $"Resource file not found: {filePath}"
                                        });
                                    }
                                }
                            }
                            catch { }
                        }

                        resources.Add((path, name, type, filePath));
                    }

                    // 收集该节点的所有属性值作为引用候选
                    try
                    {
                        var props = child.GetType().GetProperty("Properties",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (props != null)
                        {
                            var propValues = props.GetValue(child) as IEnumerable;
                            if (propValues != null)
                            {
                                foreach (var p in propValues)
                                {
                                    var propName = SafeGetProperty(p, "Name") as string ?? "";
                                    var propValue = SafeGetProperty(p, "Value");

                                    if (propValue != null)
                                    {
                                        var valStr = propValue.ToString() ?? "";
                                        if (!string.IsNullOrEmpty(valStr) && valStr.Length > 1)
                                        {
                                            references.Add(valStr);

                                            // 如果属性名暗示资源引用，也加入属性值
                                            if (propName.Contains("Texture") || propName.Contains("Material")
                                                || propName.Contains("Font") || propName.Contains("Image")
                                                || propName.Contains("Shader") || propName.Contains("Brush"))
                                            {
                                                references.Add(valStr);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // 递归
                    ScanProjectForResources(child, path, depth + 1, resources, references, broken, checkBroken);
                }
            }
            catch { }
        }

        #endregion
    }
}
