using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Rightware.Kanzi.Studio.PluginInterface;

// 调用边界规则:
// - SDK 路径（本文件）仅使用基础类型转换，不调用 Wrapper 解包方法。
// - Wrapper 解包（Reflection.cs 中的 SafeConvertValue 等）仅供反射降级路径使用。
// - 任何新增代码如需解包 Wrapper 值，必须显式调用 Reflection.SafeConvertValue。
// - CoercePropertyValueForSdk 仅处理基础类型透传，禁止引入 LocalizedString/Wrapper 构造。

namespace KanziMcpPlugin.Services
{
    /// <summary>
    /// Kanzi PluginInterface SDK 访问层 — SDK 主路径 + 薄 legacy fallback。
    /// </summary>
    public partial class KanziService
    {
        #region SDK 项目 / 节点访问

        private Project? GetSdkProject()
        {
            if (_studio == null)
                return null;

            try
            {
                return _studio.ActiveProject;
            }
            catch (Exception ex)
            {
                Log($"GetSdkProject failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>兼容 legacy 调用方 — 返回 SDK Project 或 reflection fallback。</summary>
        private object? GetActiveProject()
        {
            var project = GetSdkProject();
            if (project != null)
                return project;

            Log("GetActiveProject: SDK path failed, falling back to legacy reflection");
            return GetActiveProjectLegacyReflection();
        }

        private ProjectItem? GetSdkItem(string path)
        {
            var project = GetSdkProject();
            if (project == null)
                return null;

            if (string.IsNullOrEmpty(path))
                return project;

            var relativePath = StripProjectPrefix(path);
            try
            {
                if (string.IsNullOrEmpty(relativePath))
                    return project;

                return project.GetProjectItem(relativePath);
            }
            catch (Exception ex)
            {
                Log($"GetSdkItem('{path}' -> '{relativePath}') failed: {ex.Message}");
                return null;
            }
        }

        private object? GetProjectItem(string path)
        {
            var item = GetSdkItem(path);
            if (item != null)
                return item;

            Log($"GetProjectItem('{path}'): SDK path missed, falling back to legacy reflection");
            return GetProjectItemLegacyReflection(path);
        }

        private static PropertyContainer? AsPropertyContainer(object? item)
            => item as PropertyContainer;

        private static BindingHost? AsBindingHost(object? item)
            => item as BindingHost;

        private static ProjectItem? AsProjectItem(object? item)
            => item as ProjectItem;

        #region 非标准容器检测

        /// <summary>
        /// 非标准容器类型名称 — 这些容器在 SDK 中没有统一的 Children 实现，
        /// 其子项通过 Items/ProjectItems 等属性暴露，需直接走反射兜底。
        /// </summary>
        private static readonly HashSet<string> NonStandardContainerTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "TextureLibrary",
            "ImageDirectory",
            "FontLibrary",
            "MaterialLibrary",
            "ShaderLibrary",
            "ResourceDirectory",
            "AnimationLibrary",
            "MeshLibrary",
        };

        /// <summary>
        /// 生产模式开关 — 通过环境变量 KANZI_MCP_PRODUCTION=1 关闭 SDK vs 反射的数量校验，
        /// 避免生产环境双重遍历的性能开销。
        /// </summary>
        private static bool IsProductionMode =>
            Environment.GetEnvironmentVariable("KANZI_MCP_PRODUCTION") == "1";

        private static bool IsNonStandardContainer(object item)
        {
            if (item is not ProjectItem)
                return false;
            var typeName = item.GetType().Name;
            return NonStandardContainerTypes.Contains(typeName) ||
                   typeName.EndsWith("Library", StringComparison.OrdinalIgnoreCase) ||
                   typeName.EndsWith("Directory", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        private List<object> GetChildren(object projectItem)
        {
            // 非标准容器（TextureLibrary 等）直接走反射，避免 SDK Children 实现不完整
            if (IsNonStandardContainer(projectItem))
            {
                var containerType = projectItem.GetType().Name;
                Log($"GetChildren: non-standard container '{containerType}', using reflection directly");
                return GetChildrenLegacyReflection(projectItem);
            }

            if (projectItem is ProjectItem sdkItem)
            {
                try
                {
                    var sdkChildren = sdkItem.Children?.Cast<object>().ToList();
                    if (sdkChildren != null && sdkChildren.Count > 0)
                    {
                        // 开发模式：数量校验，确保 SDK 结果与反射兜底一致
                        if (!IsProductionMode)
                        {
                            var legacyChildren = GetChildrenLegacyReflection(projectItem);
                            if (legacyChildren.Count != sdkChildren.Count)
                            {
                                Log($"WARNING: GetChildren count mismatch for '{sdkItem.Name}' " +
                                    $"({sdkItem.GetType().Name}): SDK={sdkChildren.Count}, " +
                                    $"Reflection={legacyChildren.Count}. Falling back to reflection.");
                                return legacyChildren;
                            }
                        }
                        return sdkChildren;
                    }
                }
                catch (Exception ex)
                {
                    Log($"GetChildren SDK failed on '{sdkItem.Name}': {ex.Message}");
                }
            }

            return GetChildrenLegacyReflection(projectItem);
        }

        private string GetItemName(object item)
        {
            if (item is ProjectItem sdkItem)
            {
                if (!string.IsNullOrEmpty(sdkItem.Name))
                    return sdkItem.Name;
                if (!string.IsNullOrEmpty(sdkItem.TypeDisplayName))
                    return sdkItem.TypeDisplayName;
            }

            return GetItemNameLegacyReflection(item);
        }

        private string GetItemPath(object item)
        {
            if (item is ProjectItem sdkItem)
            {
                if (!string.IsNullOrEmpty(sdkItem.Path))
                    return sdkItem.Path;
            }

            return GetItemPathLegacyReflection(item);
        }

        private string GetItemType(object item)
        {
            if (item is ProjectItem sdkItem)
            {
                if (!string.IsNullOrEmpty(sdkItem.TypeDisplayName))
                    return sdkItem.TypeDisplayName;
            }

            return GetItemTypeLegacyReflection(item);
        }

        private PropertyTypeLibrary? GetSdkPropertyTypeLibrary()
        {
            var project = GetSdkProject();
            return project?.PropertyTypeLibrary;
        }

        #endregion

        #region SDK 属性读写

        private bool TryGetPropertyValueViaSdk(object item, string propertyName, out object? value)
        {
            value = null;
            var container = AsPropertyContainer(item);
            if (container == null)
                return false;

            try
            {
                value = container.Get(propertyName);
                return true;
            }
            catch (Exception ex)
            {
                Log($"TryGetPropertyValueViaSdk('{propertyName}') failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// SDK 属性写入统一入口 — PropertyContainer.Set 优先。
        /// Text / LocalizedString 属性强制返回 false，由调用方降级到反射策略链。
        /// </summary>
        private bool TryApplyPropertyViaSdk(object item, string propertyName, object? newValue, bool force,
            out string appliedVia, out string? error)
        {
            appliedVia = "";
            error = null;

            // ═══════════════════════════════════════════════════════════
            // Text / LocalizedString 永久隔离区（双重防御）
            // SDK 无法处理跨程序集 LocalizedString 构造，强制降级到反射。
            // ═══════════════════════════════════════════════════════════

            // 防御层 1: 属性名检测
            if (propertyName.Equals("Text", StringComparison.OrdinalIgnoreCase)
                || propertyName.EndsWith(".Text", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("TextConcept", StringComparison.OrdinalIgnoreCase))
            {
                error = "Text property requires reflection fallback (LocalizedString cross-assembly)";
                Log($"TryApplyPropertyViaSdk: Text/LocalizedString property '{propertyName}' blocked from SDK path, falling back to reflection.");
                return false;
            }

            // 防御层 2: 值类型检测（LocalizedString 跨程序集类型）
            if (newValue != null)
            {
                var valueType = newValue.GetType();
                if (valueType.Name.Contains("LocalizedString", StringComparison.OrdinalIgnoreCase)
                    || (valueType.FullName?.Contains("LocalizedString", StringComparison.OrdinalIgnoreCase) == true))
                {
                    error = "LocalizedString value requires reflection fallback (cross-assembly type)";
                    Log($"TryApplyPropertyViaSdk: LocalizedString value type '{valueType.FullName ?? valueType.Name}' blocked from SDK path, falling back to reflection.");
                    return false;
                }
            }

            var container = AsPropertyContainer(item);
            if (container == null)
            {
                error = "Item is not a PropertyContainer";
                return false;
            }

            try
            {
                if (force && item is ProjectItem projectItem)
                    TryUnlockPropertyViaSdk(projectItem, propertyName);

                try
                {
                    container.Set(propertyName, newValue ?? "");
                    appliedVia = "SdkPropertyContainer";
                    return true;
                }
                catch
                {
                    var coerced = CoercePropertyValueForSdk(newValue, propertyName);
                    if (coerced == null || ReferenceEquals(coerced, newValue))
                        throw;

                    container.Set(propertyName, coerced);
                    appliedVia = "SdkPropertyContainer";
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log($"TryApplyPropertyViaSdk('{propertyName}') failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 基础类型值转换 — 仅处理 string/int/double/bool 等基础类型。
        /// LocalizedString 构造已永久隔离到 Properties.cs 反射区域。
        /// </summary>
        private static object? CoercePropertyValueForSdk(object? newValue, string propertyName)
        {
            // LocalizedString 构造已由 TryApplyPropertyViaSdk 双重防御阻断，此处仅做基础类型透传。
            _ = propertyName; // 保留参数签名兼容
            return newValue;
        }

        private void TryUnlockPropertyViaSdk(ProjectItem item, string propertyName)
        {
            try
            {
                if (!item.HasProperty(propertyName))
                    return;

                foreach (var prop in item.GetFixedProperties()
                    .Concat(item.GetContextProperties())
                    .Concat(item.GetFrequentlyAddedProperties()))
                {
                    if (!string.Equals(prop.Name, propertyName, StringComparison.Ordinal))
                        continue;

                    if (item.IsPropertyReadOnly(prop))
                        item.SetPropertyReadOnlyStatus(prop, false);
                    break;
                }
            }
            catch (Exception ex)
            {
                Log($"TryUnlockPropertyViaSdk('{propertyName}') failed: {ex.Message}");
            }
        }

        private Dictionary<string, object?> GetItemPropertiesViaSdk(object item)
        {
            var props = new Dictionary<string, object?>();
            var container = AsPropertyContainer(item);
            if (container == null)
                return props;

            try
            {
                foreach (var prop in container.Properties)
                {
                    if (prop == null || string.IsNullOrEmpty(prop.Name))
                        continue;

                    try
                    {
                        var val = container.Get(prop.Name);
                        props[prop.Name] = SafeConvertValue(val);
                    }
                    catch
                    {
                        props[prop.Name] = "(unable to read)";
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetItemPropertiesViaSdk failed: {ex.Message}");
            }

            return props;
        }

        #endregion

        #region SDK 绑定

        private List<Dictionary<string, object?>> GetBindingsViaSdk(object item)
        {
            var bindings = new List<Dictionary<string, object?>>();
            var host = AsBindingHost(item);
            if (host == null)
                return bindings;

            try
            {
                foreach (var binding in host.Bindings)
                {
                    if (binding == null)
                        continue;

                    bindings.Add(new Dictionary<string, object?>
                    {
                        ["property"] = ExtractBindingPropertyFromSdk(binding.Property),
                        ["code"] = binding.Code ?? "",
                        ["mode"] = binding.IsBindingActive ? "Active" : "Inactive",
                        ["enabled"] = binding.IsBindingEnabled,
                        ["codeValid"] = binding.IsCodeValid
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"GetBindingsViaSdk failed: {ex.Message}");
            }

            return bindings;
        }

        private List<object> GetBindingsList(object item)
        {
            var host = AsBindingHost(item);
            if (host != null)
            {
                try
                {
                    return host.Bindings?.Cast<object>().ToList() ?? new List<object>();
                }
                catch (Exception ex)
                {
                    Log($"GetBindingsList SDK failed: {ex.Message}");
                }
            }

            return new List<object>();
        }

        /// <summary>
        /// 统一绑定读取主入口 — SDK 优先，失败时降级到反射兜底。
        /// 返回标准化 3 字段格式 (property/code/mode)，保证与旧反射行为完全一致。
        /// </summary>
        private List<Dictionary<string, object?>> GetBindings(object item)
        {
            var sdk = GetBindingsViaSdk(item);
            if (sdk.Count > 0)
            {
                // 标准化为 legacy 3 字段格式
                return sdk.Select(b => new Dictionary<string, object?>
                {
                    ["property"] = b.TryGetValue("property", out var p) ? p : "unknown",
                    ["code"] = b.TryGetValue("code", out var c) ? c : "",
                    ["mode"] = b.TryGetValue("mode", out var m) ? m : "OneWay"
                }).ToList();
            }
            return GetBindingsInfoLegacyReflection(item);
        }

        private List<Dictionary<string, object?>> GetBindingsInfoLegacyReflection(object item)
        {
            var bindings = new List<Dictionary<string, object?>>();
            var bindingsProp = item.GetType().GetProperty("Bindings",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (bindingsProp == null)
                return bindings;

            try
            {
                if (bindingsProp.GetValue(item) is not IEnumerable bindingsCollection)
                    return bindings;

                foreach (var binding in bindingsCollection)
                {
                    try
                    {
                        bindings.Add(new Dictionary<string, object?>
                        {
                            ["property"] = ExtractBindingProperty(SafeGetProperty(binding, "Property")),
                            ["code"] = SafeGetProperty(binding, "Code") as string ?? "",
                            ["mode"] = SafeGetProperty(binding, "Mode")?.ToString() ?? "OneWay"
                        });
                    }
                    catch { }
                }
            }
            catch { }

            return bindings;
        }

        private bool TrySetBindingCodeViaSdk(object binding, string newCode, out string error)
        {
            error = "";
            if (binding is not Binding sdkBinding)
                return false;

            try
            {
                sdkBinding.Code = newCode;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to set binding code via SDK: {ex.Message}";
                return false;
            }
        }

        private static string ExtractBindingPropertyFromSdk(Property? property)
        {
            if (property == null)
                return "unknown";

            if (!string.IsNullOrEmpty(property.LocalName))
                return property.LocalName;
            if (!string.IsNullOrEmpty(property.Name))
                return property.Name;
            if (!string.IsNullOrEmpty(property.DisplayName))
                return property.DisplayName;

            return "unknown";
        }

        #endregion

        #region SDK 资源导入

        private bool TryImportImagesViaSdk(Project project, IEnumerable<string> filePaths, out Exception? error)
        {
            error = null;
            if (_studio == null)
            {
                error = new InvalidOperationException("Kanzi Studio not connected");
                return false;
            }

            try
            {
                // ImportImages(project, filePaths, copyToProject: false)
                // — false = 不自动复制，由调用方在 Images 目录准备文件后传入项目内路径
                _studio.Commands.ImportImages(project, filePaths, false);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                Log($"TryImportImagesViaSdk failed: {ex.Message}");
                return false;
            }
        }

        private bool TryImportSingleImageViaSdk(Project project, string filePath, out Exception? error)
        {
            error = null;
            if (_studio == null)
            {
                error = new InvalidOperationException("Kanzi Studio not connected");
                return false;
            }

            try
            {
                _studio.Commands.ImportImages(project, filePath, false);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                Log($"TryImportSingleImageViaSdk failed: {ex.Message}");
                return false;
            }
        }

        private bool TryImportAsset3DViaSdk(Asset3DSourceFile sourceFile, string filePath, out Exception? error)
        {
            error = null;
            if (_studio == null)
            {
                error = new InvalidOperationException("Kanzi Studio not connected");
                return false;
            }

            try
            {
                _studio.Commands.ImportAsset3DSourceFile(sourceFile, filePath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                Log($"TryImportAsset3DViaSdk failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region SDK 资源诊断

        private bool IsResourceReferencedViaSdk(Project project, ProjectItem resourceItem)
        {
            try
            {
                var kzbUrl = resourceItem.KzbUrl;
                if (string.IsNullOrEmpty(kzbUrl))
                    return false;

                var referrers = project.GetReferringItemsKzbNames(kzbUrl);
                return referrers != null && referrers.Any();
            }
            catch (Exception ex)
            {
                Log($"IsResourceReferencedViaSdk failed for '{GetItemName(resourceItem)}': {ex.Message}");
                return false;
            }
        }

        /// <summary>SDK 树遍历 — 从 Project 根收集属性中的资源引用。</summary>
        private void CollectUsedResourcesFromSdkTree(ProjectItem item, HashSet<string> usedPaths,
            ref int scannedNodeCount, ref int detectedRefCount, List<string> sampleReferences, int depth)
        {
            if (depth > 30)
                return;

            try
            {
                scannedNodeCount++;

                if (item is PropertyContainer container)
                {
                    foreach (var prop in container.Properties)
                    {
                        if (prop == null || string.IsNullOrEmpty(prop.Name))
                            continue;

                        try
                        {
                            TryCollectResourceReferenceFromValue(container.Get(prop.Name), usedPaths,
                                ref detectedRefCount, sampleReferences, prop.Name);
                        }
                        catch { }
                    }
                }

                TryExtractDirectRefs(item, usedPaths, ref detectedRefCount, sampleReferences);

                foreach (var child in item.Children ?? Enumerable.Empty<ProjectItem>())
                    CollectUsedResourcesFromSdkTree(child, usedPaths, ref scannedNodeCount, ref detectedRefCount,
                        sampleReferences, depth + 1);
            }
            catch (Exception ex)
            {
                Log($"CollectUsedResourcesFromSdkTree failed on '{item.Name}': {ex.Message}");
            }
        }

        /// <summary>用 GetReferringItemsKzbNames 反向标记被引用的资源路径。</summary>
        private int EnrichUsedPathsFromSdkReferrers(Project project, IEnumerable<string> folderNames,
            HashSet<string> usedPaths, List<string> sampleReferences)
        {
            var added = 0;
            foreach (var folderName in folderNames)
            {
                ProjectItem? folder = null;
                try
                {
                    folder = project.GetProjectItem(folderName);
                }
                catch { }

                if (folder == null)
                    continue;

                added += EnrichUsedPathsFromSdkReferrersRecursive(project, folder, usedPaths, sampleReferences);
            }

            return added;
        }

        private int EnrichUsedPathsFromSdkReferrersRecursive(Project project, ProjectItem folder,
            HashSet<string> usedPaths, List<string> sampleReferences)
        {
            var added = 0;
            foreach (var child in folder.Children ?? Enumerable.Empty<ProjectItem>())
            {
                var childType = !string.IsNullOrEmpty(child.TypeDisplayName)
                    ? child.TypeDisplayName
                    : child.GetType().Name;

                if (IsResourceFolder(child, childType))
                {
                    added += EnrichUsedPathsFromSdkReferrersRecursive(project, child, usedPaths, sampleReferences);
                    continue;
                }

                if (string.IsNullOrEmpty(child.KzbUrl))
                    continue;

                try
                {
                    var referrers = project.GetReferringItemsKzbNames(child.KzbUrl);
                    if (referrers == null || !referrers.Any())
                        continue;

                    var normalizedPath = NormalizeResourcePath(child.Path ?? child.Name);
                    if (!string.IsNullOrEmpty(normalizedPath) && usedPaths.Add(normalizedPath))
                    {
                        added++;
                        if (sampleReferences.Count < 10)
                            sampleReferences.Add($"SDK.GetReferringItemsKzbNames → {normalizedPath}");
                    }

                    if (!string.IsNullOrEmpty(child.Name) && usedPaths.Add(child.Name))
                        added++;
                }
                catch (Exception ex)
                {
                    Log($"EnrichUsedPathsFromSdkReferrers failed for '{child.Name}': {ex.Message}");
                }
            }

            return added;
        }

        #endregion

        #region SDK 资源定位 / FBX

        private static bool IsSdkTextureProjectItem(ProjectItem item)
        {
            var type = item.TypeDisplayName ?? item.GetType().Name;
            return type.Contains("Texture", StringComparison.OrdinalIgnoreCase);
        }

        private static ProjectItem? FindChildByNameRecursive(ProjectItem root, string resourceName)
        {
            foreach (var child in root.Children ?? Enumerable.Empty<ProjectItem>())
            {
                if (string.Equals(child.Name, resourceName, StringComparison.OrdinalIgnoreCase))
                    return child;

                var nested = FindChildByNameRecursive(child, resourceName);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        /// <summary>
        /// SDK ImportImages 后定位资源 — TextureLibrary、ImageDirectory、路径别名。
        /// </summary>
        private ProjectItem? FindImportedImageAfterSdkImport(Project project, string resourceName)
        {
            foreach (var candidatePath in new[]
                     {
                         $"Textures/{resourceName}",
                         $"Images/{resourceName}",
                         resourceName,
                         $"Textures/Images/{resourceName}"
                     })
            {
                try
                {
                    var item = project.GetProjectItem(candidatePath);
                    if (item != null)
                        return item;
                }
                catch { }
            }

            try
            {
                var textures = project.TextureLibrary;
                if (textures != null)
                {
                    var found = FindChildByNameRecursive(textures, resourceName);
                    if (found != null)
                        return found;
                }
            }
            catch (Exception ex)
            {
                Log($"FindImportedImageAfterSdkImport TextureLibrary failed: {ex.Message}");
            }

            try
            {
                var imageDir = project.ImageDirectory;
                if (imageDir != null)
                {
                    var found = FindChildByNameRecursive(imageDir, resourceName);
                    if (found != null)
                        return found;
                }
            }
            catch (Exception ex)
            {
                Log($"FindImportedImageAfterSdkImport ImageDirectory failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>SDK 导入成功后完成结果构建，避免重复 legacy 导入。</summary>
        private bool TryCompleteSdkImageImport(
            Project sdkProject,
            object projectWrapper,
            object texturesFolder,
            string filePath,
            string effectiveName,
            out ImportImageResult? result)
        {
            result = null;
            var located = FindImportedImageAfterSdkImport(sdkProject, effectiveName);
            if (located == null)
                return false;

            if (IsSdkTextureProjectItem(located))
            {
                result = BuildImportSuccess(located, filePath, "Commands.ImportImages.SDK");
                return true;
            }

            var logicProject = ResolveLogicProject(GetInternalProjectItem(projectWrapper) ?? projectWrapper)
                               ?? projectWrapper;
            var textureParent = ResolveTextureParent(logicProject, texturesFolder, "Textures");
            var imageFileObj = GetInternalProjectItem(located) ?? located;

            if (textureParent != null)
            {
                var texture = TryCreateTextureFromImageFile(logicProject, textureParent, texturesFolder,
                    imageFileObj, effectiveName, filePath, filePath);
                if (texture != null)
                {
                    result = BuildImportSuccess(texture, filePath, "Commands.ImportImages.SDK.ImageFile+Texture");
                    return true;
                }
            }

            result = BuildImportSuccess(located, filePath, "Commands.ImportImages.SDK.ImageFile");
            return true;
        }

        private void TryCollectResourceReferenceFromValue(object? value, HashSet<string> usedPaths,
            ref int detectedRefCount, List<string> sampleReferences, string label)
        {
            if (value == null)
                return;

            if (value is ProjectItem refItem)
            {
                RegisterUsedResourcePath(refItem.Path ?? refItem.Name, usedPaths, ref detectedRefCount,
                    sampleReferences, $"{label} → ProjectItem");
                if (!string.IsNullOrEmpty(refItem.KzbUrl))
                    RegisterUsedResourcePath(refItem.KzbUrl, usedPaths, ref detectedRefCount, sampleReferences,
                        $"{label} → KzbUrl");
                return;
            }

            if (value is string s)
            {
                if (s == "(unable to read)")
                    return;
                if (s.StartsWith("kzb://", StringComparison.OrdinalIgnoreCase))
                {
                    RegisterUsedResourcePath(s, usedPaths, ref detectedRefCount, sampleReferences, label);
                    return;
                }
            }

            var extracted = ExtractResourceReference(value);
            if (!string.IsNullOrEmpty(extracted))
            {
                RegisterUsedResourcePath(extracted, usedPaths, ref detectedRefCount, sampleReferences, label);
                return;
            }

            var kzbUrl = SafeGetProperty(value, "KzbUrl") as string;
            if (!string.IsNullOrEmpty(kzbUrl))
                RegisterUsedResourcePath(kzbUrl, usedPaths, ref detectedRefCount, sampleReferences, $"{label}.KzbUrl");
        }

        private static void RegisterUsedResourcePath(string? rawPath, HashSet<string> usedPaths,
            ref int detectedRefCount, List<string> sampleReferences, string label)
        {
            if (string.IsNullOrEmpty(rawPath))
                return;

            var normalized = NormalizeResourcePath(rawPath);
            if (string.IsNullOrEmpty(normalized))
                return;

            if (!usedPaths.Add(normalized))
                return;

            detectedRefCount++;
            if (sampleReferences.Count < 10)
                sampleReferences.Add($"{label} → {normalized}");
        }

        private object? FindImportedTextureAfterSdkImport(Project project, string resourceName)
            => FindImportedImageAfterSdkImport(project, resourceName);

        private string EffectiveImportResourceName(string filePath, string? resourceName, bool singleFileBatch)
        {
            if (singleFileBatch && !string.IsNullOrEmpty(resourceName))
                return resourceName;
            return Path.GetFileNameWithoutExtension(filePath);
        }

        private bool TryImportFbxViaSdk(Project project, string filePath, string? resourceName,
            out ProjectItem? imported)
        {
            imported = null;
            if (_studio == null)
                return false;

            try
            {
                var dir = project.Asset3DImportSourceDirectory;
                if (dir == null)
                    return false;

                var name = resourceName ?? Path.GetFileNameWithoutExtension(filePath);
                var sourceFile = project.CreateProjectItem<Asset3DSourceFile>(name, dir);
                if (sourceFile == null)
                    return false;

                _studio.Commands.ImportAsset3DSourceFile(sourceFile, filePath);
                imported = sourceFile;
                return true;
            }
            catch (Exception ex)
            {
                Log($"TryImportFbxViaSdk failed: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
