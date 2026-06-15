using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

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

                var checkPriority = !args.HasValue
                    || !args.Value.TryGetProperty("checkPriority", out var cp)
                    || cp.GetBoolean();
                var findOrphans = !args.HasValue
                    || !args.Value.TryGetProperty("findOrphans", out var fo)
                    || fo.GetBoolean();

                string? auditPath = null;
                if (args.HasValue && args.Value.TryGetProperty("path", out var pathEl)
                    && pathEl.ValueKind == JsonValueKind.String)
                {
                    auditPath = pathEl.GetString();
                }

                object auditRoot = project;
                if (!string.IsNullOrWhiteSpace(auditPath))
                {
                    var rootItem = GetProjectItem(auditPath);
                    if (rootItem == null)
                        return ErrorJson($"Node not found: {auditPath}");
                    auditRoot = rootItem;
                }

                var modificationResults = new List<Dictionary<string, object?>>();
                if (args.HasValue && args.Value.TryGetProperty("modifications", out var modsEl)
                    && modsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var modEl in modsEl.EnumerateArray())
                        modificationResults.Add(ProcessBindingModification(modEl));
                }

                var issues = new List<Dictionary<string, object?>>();
                var totalBindings = 0;
                var bindingCodes = new Dictionary<string, List<string>>();
                var rootPathPrefix = string.IsNullOrWhiteSpace(auditPath) ? "" : auditPath.Trim('/');

                AuditBindingsRecursive(auditRoot, rootPathPrefix, issues, ref totalBindings,
                    bindingCodes, checkPriority, findOrphans, 0);

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

                var orphanBindings = new List<Dictionary<string, object?>>();
                if (findOrphans)
                {
                    foreach (var issue in issues.Where(i => i["type"]?.ToString() == "orphan"))
                        orphanBindings.Add(issue);
                }

                var recommendations = new List<string>();
                if (issues.Any(i => i["type"]?.ToString() == "orphan"))
                    recommendations.Add("Clean up bindings whose target property could not be resolved");
                if (priorityConflicts.Count > 0)
                    recommendations.Add($"Found {priorityConflicts.Count} priority conflicts - same binding code used by multiple nodes");
                if (modificationResults.Any(m =>
                    {
                        if (m.TryGetValue("applied", out var applied))
                            return applied is true;
                        return false;
                    }))
                    recommendations.Add("Binding modifications were applied — verify data context in Kanzi Studio");

                return SafeSerialize(new
                {
                    success = true,
                    auditRoot = string.IsNullOrWhiteSpace(auditPath) ? "(project root)" : auditPath,
                    totalBindings,
                    issues = issues.Take(200),
                    priorityConflicts,
                    orphanBindings,
                    modificationResults = modificationResults.Count > 0 ? modificationResults : null,
                    recommendations
                });
            }
            catch (Exception ex)
            {
                return ErrorJson($"Audit bindings failed: {ex.Message}");
            }
        }

        private Dictionary<string, object?> ProcessBindingModification(JsonElement modEl)
        {
            var nodePath = modEl.TryGetProperty("nodePath", out var np) ? np.GetString() ?? "" : "";
            var mode = modEl.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() ?? "preview" : "preview";
            var bindingIndex = modEl.TryGetProperty("bindingIndex", out var idxEl) && idxEl.TryGetInt32(out var idx)
                ? idx
                : (int?)null;
            var propertyName = modEl.TryGetProperty("property", out var propEl) ? propEl.GetString() : null;
            var newCode = modEl.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;

            var result = new Dictionary<string, object?>
            {
                ["nodePath"] = nodePath,
                ["mode"] = mode,
                ["applied"] = false
            };

            if (string.IsNullOrWhiteSpace(nodePath))
            {
                result["success"] = false;
                result["error"] = "nodePath is required";
                return result;
            }

            if (string.IsNullOrWhiteSpace(newCode))
            {
                result["success"] = false;
                result["error"] = "code is required for binding modification";
                return result;
            }

            if (bindingIndex == null && string.IsNullOrWhiteSpace(propertyName))
            {
                result["success"] = false;
                result["error"] = "bindingIndex or property is required to identify the binding";
                return result;
            }

            var item = GetProjectItem(nodePath);
            if (item == null)
            {
                result["success"] = false;
                result["error"] = $"Node not found: {nodePath}";
                return result;
            }

            var bindings = GetBindingsList(item);
            if (bindings.Count == 0)
            {
                result["success"] = false;
                result["error"] = "Node has no bindings";
                return result;
            }

            object? targetBinding = null;
            int resolvedIndex = -1;

            if (bindingIndex.HasValue)
            {
                if (bindingIndex.Value < 0 || bindingIndex.Value >= bindings.Count)
                {
                    result["success"] = false;
                    result["error"] = $"bindingIndex {bindingIndex.Value} out of range (0..{bindings.Count - 1})";
                    return result;
                }
                resolvedIndex = bindingIndex.Value;
                targetBinding = bindings[resolvedIndex];
            }
            else
            {
                for (var i = 0; i < bindings.Count; i++)
                {
                    var prop = ExtractBindingProperty(SafeGetProperty(bindings[i], "Property"));
                    if (string.Equals(prop, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedIndex = i;
                        targetBinding = bindings[i];
                        break;
                    }
                }

                if (targetBinding == null)
                {
                    result["success"] = false;
                    result["error"] = $"No binding found for property '{propertyName}'";
                    return result;
                }
            }

            var oldCode = SafeGetProperty(targetBinding, "Code") as string ?? "";
            var targetProperty = ExtractBindingProperty(SafeGetProperty(targetBinding, "Property"));

            result["bindingIndex"] = resolvedIndex;
            result["property"] = targetProperty;
            result["oldCode"] = oldCode;
            result["newCode"] = newCode;

            if (string.Equals(oldCode, newCode, StringComparison.Ordinal))
            {
                result["success"] = true;
                result["message"] = "Code unchanged";
                result["bindings"] = SerializeBindingsSnapshot(bindings);
                return result;
            }

            if (string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "dry-run", StringComparison.OrdinalIgnoreCase))
            {
                result["success"] = true;
                result["message"] = "Preview only — no changes applied";
                result["bindings"] = SerializeBindingsSnapshot(bindings);
                return result;
            }

            if (!TrySetBindingCode(targetBinding, newCode, out var setError))
            {
                result["success"] = false;
                result["error"] = setError;
                return result;
            }

            bindings = GetBindingsList(item);
            result["success"] = true;
            result["applied"] = true;
            result["message"] = "Binding code updated";
            result["bindings"] = SerializeBindingsSnapshot(bindings);
            return result;
        }

        private List<object> GetBindingsList(object item)
        {
            var list = new List<object>();
            var bindingsProp = item.GetType().GetProperty("Bindings",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (bindingsProp == null)
                return list;

            if (bindingsProp.GetValue(item) is not IEnumerable bindings)
                return list;

            foreach (var binding in bindings)
                list.Add(binding);
            return list;
        }

        private List<Dictionary<string, object?>> SerializeBindingsSnapshot(IReadOnlyList<object> bindings)
        {
            var snapshot = new List<Dictionary<string, object?>>();
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                snapshot.Add(new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["property"] = ExtractBindingProperty(SafeGetProperty(binding, "Property")),
                    ["code"] = SafeGetProperty(binding, "Code") as string ?? "",
                    ["mode"] = SafeGetProperty(binding, "Mode")?.ToString() ?? "OneWay"
                });
            }
            return snapshot;
        }

        private bool TrySetBindingCode(object binding, string newCode, out string error)
        {
            error = "";
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var type = binding.GetType();

            try
            {
                var codeProp = type.GetProperty("Code", bf);
                if (codeProp != null && codeProp.CanWrite)
                {
                    codeProp.SetValue(binding, newCode);
                    return true;
                }

                foreach (var methodName in new[] { "SetCode", "set_Code" })
                {
                    var method = type.GetMethod(methodName, bf, null, new[] { typeof(string) }, null);
                    if (method != null)
                    {
                        method.Invoke(binding, new object[] { newCode });
                        return true;
                    }
                }

                error = "Binding object does not expose a writable Code property";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Failed to set binding code: {ex.Message}";
                return false;
            }
        }

        private void AuditBindingsRecursive(object parent, string parentPath,
            List<Dictionary<string, object?>> issues, ref int totalBindings,
            Dictionary<string, List<string>> bindingCodes,
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

                                    if (checkPriority && !string.IsNullOrWhiteSpace(code))
                                    {
                                        if (!bindingCodes.ContainsKey(code))
                                            bindingCodes[code] = new List<string>();
                                        bindingCodes[code].Add($"{path}:{propertyName}");
                                    }

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
                        bindingCodes, checkPriority, findOrphans, depth + 1);
                }
            }
            catch { }
        }

        /// <summary>Deprecated stub — localization audit removed.</summary>
        public string AuditLocalizationDeprecated(JsonElement? args)
        {
            _ = args;
            return AuditCompatMapper.BuildLocalizationDeprecatedJson();
        }

        /// <summary>Compat wrapper — forwards to DoctorResource and maps to legacy schema.</summary>
        public string AuditResourceReferencesCompat(JsonElement? args)
        {
            var checkUnused = !args.HasValue || !args.Value.TryGetProperty("checkUnused", out var cu) || cu.GetBoolean();
            var checkBroken = !args.HasValue || !args.Value.TryGetProperty("checkBroken", out var cb) || cb.GetBoolean();
            var checkOrphaned = !args.HasValue || !args.Value.TryGetProperty("checkOrphaned", out var co) || co.GetBoolean();

            var doctorArgs = AuditCompatMapper.BuildDoctorArgs(checkUnused, checkBroken, checkOrphaned);
            var doctorJson = DoctorResource(doctorArgs);
            return AuditCompatMapper.MapDoctorJsonToResourceReferencesCompat(doctorJson, checkOrphaned);
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

        #endregion
    }
}



