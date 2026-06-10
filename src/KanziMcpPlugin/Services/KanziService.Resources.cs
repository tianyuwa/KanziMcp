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

                // Convert to internal types
                var internalProject = GetInternalProjectItem(project);
                Log($"ImportImage: project={project.GetType().Name}, internalProject={internalProject?.GetType().Name}");

                // Find or create Textures folder
                var texturesFolder = GetOrCreateResourceFolder(project, targetFolder);
                if (texturesFolder == null)
                    return ErrorJson($"Cannot find or create resource folder: {targetFolder}");
                Log($"ImportImage: found textures folder: {GetItemName(texturesFolder)}");

                // Step 1: Copy the file to the project's Images directory, then import it
                var imageFilePath = CopyFileToProjectImages(internalProject ?? project, filePath);
                Log($"ImportImage: local image path for import: {imageFilePath}");

                // Step 2: Try to import the image file into ImageDirectory to create an ImageFile
                object? imageFile = TryImportImageFile(internalProject ?? project, imageFilePath ?? filePath);

                // Step 2: Clone DefaultTexture
                object? importedItem = null;
                try
                {
                    var defaultTexProp = (internalProject ?? project).GetType().GetProperty("DefaultTexture",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (defaultTexProp != null)
                    {
                        var defaultTexture = defaultTexProp.GetValue(internalProject ?? project);
                        Log($"ImportImage: DefaultTexture={defaultTexture?.GetType().Name}");

                        if (defaultTexture != null)
                        {
                            var internalTemplate = GetInternalProjectItem(defaultTexture);
                            if (internalTemplate != null)
                            {
                                var cloneMethod = internalTemplate.GetType().GetMethod("CloneUnder",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (cloneMethod != null)
                                {
                                    var pars = cloneMethod.GetParameters();
                                    var cloneName = resourceName ?? System.IO.Path.GetFileNameWithoutExtension(filePath);
                                    var internalParent = GetInternalProjectItem(texturesFolder) ?? texturesFolder;
                                    var cloneMethodArg = Enum.GetValues(pars[2].ParameterType).GetValue(0);
                                    importedItem = cloneMethod.Invoke(internalTemplate,
                                        new[] { cloneName, internalParent, cloneMethodArg });
                                    Log($"ImportImage: CloneUnder succeeded");

                                    // Step 3: Set TextureImage to the ImageFile
                                    if (importedItem != null && imageFile != null)
                                    {
                                        SetTextureImageProperty(importedItem, imageFile);
                                    }
                                    else if (importedItem != null)
                                    {
                                        Log($"ImportImage: no ImageFile available, trying SetTextureSourceFile fallback...");
                                        SetTextureSourceFile(importedItem, filePath);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"ImportImage: CloneUnder strategy failed: {ex.Message}");
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
                CollectUsedResources(project, usedResourcePaths,
                    out var scannedNodeCount, out var detectedRefCount, out var sampleReferences);

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
                    // P3: observability diagnostics
                    ["scannedNodeCount"] = scannedNodeCount,
                    ["detectedReferenceCount"] = detectedRefCount,
                    ["sampleReferences"] = sampleReferences,
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

                // P3: warn when no references were detected despite having nodes
                if (detectedRefCount == 0 && scannedNodeCount > 0)
                {
                    Log($"DoctorResource: WARNING — scanned {scannedNodeCount} nodes but detected 0 resource references. Results may be unreliable.");
                    recommendations.Add("No resource references detected during scan. Results may not reflect actual usage — verify manually in Kanzi Studio.");
                }

                Log($"DoctorResource: scanned {scannedNodeCount} nodes, detected {detectedRefCount} refs, " +
                    $"{unusedImages.Count} unused images, {unusedTextures.Count} unused textures");

                return SafeSerialize(result);
            }
            catch (Exception ex)
            {
                Log($"DoctorResource failed: {ex.Message}");
                return ErrorJson($"诊断失败: {ex.Message}");
            }
        }

        private void CollectUsedResources(object project, HashSet<string> usedPaths,
            out int scannedNodeCount, out int detectedRefCount, out List<string> sampleReferences)
        {
            scannedNodeCount = 0;
            detectedRefCount = 0;
            sampleReferences = new List<string>();
            CollectUsedResourcesRecursive(project, usedPaths, 0,
                ref scannedNodeCount, ref detectedRefCount, sampleReferences);
        }

        private void CollectUsedResourcesRecursive(object parent, HashSet<string> usedPaths, int depth,
            ref int scannedNodeCount, ref int detectedRefCount, List<string> sampleReferences)
        {
            if (depth > 30) return;
            try
            {
                scannedNodeCount++;

                // Strategy 1: scan properties for resource references using
                // Kanzi-aware extraction (KzbUrl, ResourceUrl, NodeReference, etc.)
                var props = GetItemProperties(parent);
                foreach (var kvp in props)
                {
                    var value = kvp.Value;
                    if (value == null) continue;
                    if (value is string s && s == "(unable to read)") continue;

                    var extracted = ExtractResourceReference(value);
                    if (!string.IsNullOrEmpty(extracted))
                    {
                        var normalized = NormalizeResourcePath(extracted);
                        if (!string.IsNullOrEmpty(normalized) && usedPaths.Add(normalized))
                        {
                            detectedRefCount++;
                            if (sampleReferences.Count < 10)
                                sampleReferences.Add($"{kvp.Key} → {normalized}");
                        }
                    }
                }

                // Strategy 2: check direct C# properties that commonly hold
                // resource references (Texture, Image, Material, etc. on wrappers)
                TryExtractDirectRefs(parent, usedPaths, ref detectedRefCount, sampleReferences);

                // Recurse into children
                foreach (var child in GetChildren(parent))
                {
                    CollectUsedResourcesRecursive(child, usedPaths, depth + 1,
                        ref scannedNodeCount, ref detectedRefCount, sampleReferences);
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

                    // Recurse into sub-folders / libraries
                    if (IsResourceFolder(child, childType))
                    {
                        CollectUnusedResources(child, usedPaths, unusedImages, unusedTextures, depth + 1);
                        continue;
                    }

                    var normalizedChildPath = NormalizeResourcePath(childPath);
                    var isUsed = IsResourceUsed(childName, normalizedChildPath, usedPaths);

                    var resourceInfo = new Dictionary<string, object>
                    {
                        ["name"] = childName,
                        ["path"] = childPath,
                        ["type"] = childType,
                        ["isUsed"] = isUsed
                    };

                    // P2: classify by type — Single Texture / Cubemap Texture go to
                    // textures, not images. Image file resources (e.g. "Image" type
                    // without "Texture") go to images. This matches checkImages /
                    // checkTextures semantics.
                    if (IsImageResourceType(childType))
                    {
                        if (!isUsed)
                            unusedImages.Add(resourceInfo);
                    }
                    else if (IsTextureResourceType(childType))
                    {
                        if (!isUsed)
                            unusedTextures.Add(resourceInfo);
                    }
                    // Brushes and Materials that reference textures
                    else if (childType.Contains("Brush") || childType.Contains("Material"))
                    {
                        if (!isUsed)
                            unusedTextures.Add(resourceInfo);
                    }
                }
            }
            catch { }
        }

        // ── DoctorResource helpers ──────────────────────────────────────────

        /// <summary>
        /// Extract a resource path from a property value object.
        /// Kanzi stores references as wrapper objects (ResourceReference, NodeReference,
        /// DynamicProperty, etc.) whose ToString() rarely contains the raw path.
        /// We probe known properties (KzbUrl, ResourceUrl, Path, Name) and fall back
        /// to pattern-matching on the string representation.
        /// </summary>
        private string? ExtractResourceReference(object value)
        {
            if (value == null) return null;

            // 1. Named URL/path properties on the reference object
            foreach (var propName in new[] { "KzbUrl", "ResourceUrl", "Url", "Path" })
            {
                var s = SafeGetProperty(value, propName) as string;
                if (!string.IsNullOrEmpty(s) && LooksLikeResourcePath(s))
                    return s;
            }

            // 2. Resolve via NodeReference / ReferencedNode → get its path
            foreach (var refProp in new[] { "Target", "ReferencedNode", "Node" })
            {
                var target = SafeGetProperty(value, refProp);
                if (target != null)
                {
                    var targetPath = GetItemPath(target);
                    if (!string.IsNullOrEmpty(targetPath) && LooksLikeResourcePath(targetPath))
                        return targetPath;
                }
            }

            // 3. Name-based resolution — try to look up in the project tree
            var name = SafeGetProperty(value, "Name") as string;
            if (!string.IsNullOrEmpty(name) && LooksLikeResourceName(name))
            {
                var item = GetProjectItem(name);
                if (item != null)
                {
                    var path = GetItemPath(item);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }

            // 4. Fallback: toString + pattern match
            var str = value.ToString();
            if (!string.IsNullOrEmpty(str) && LooksLikeResourcePath(str))
                return str;

            return null;
        }

        /// <summary>Check direct C# properties on a node wrapper for resource references
        /// (e.g. Image2DPluginWrapper.Texture, Material.DiffuseMap).</summary>
        private void TryExtractDirectRefs(object node, HashSet<string> usedPaths,
            ref int detectedRefCount, List<string> sampleReferences)
        {
            var resourceProps = new[]
            {
                "Texture", "Image", "Material", "Brush",
                "DiffuseMap", "NormalMap", "SpecularMap", "EmissiveMap",
                "BaseColorTexture", "MetallicRoughnessTexture",
                "Source", "Target", "Resource"
            };

            foreach (var propName in resourceProps)
            {
                try
                {
                    var prop = node.GetType().GetProperty(propName,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (prop == null) continue;

                    var val = prop.GetValue(node);
                    if (val == null) continue;

                    var path = ExtractResourceReference(val);
                    if (!string.IsNullOrEmpty(path))
                    {
                        var normalized = NormalizeResourcePath(path);
                        if (!string.IsNullOrEmpty(normalized) && usedPaths.Add(normalized))
                        {
                            detectedRefCount++;
                            if (sampleReferences.Count < 10)
                                sampleReferences.Add($"{propName}(direct) → {normalized}");
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>Normalize a resource path for reliable comparison.</summary>
        private static string NormalizeResourcePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Replace('\\', '/').TrimStart('/').Trim();
        }

        /// <summary>Quick pre-filter: does this string look like it could be a resource path?</summary>
        private static bool LooksLikeResourcePath(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.Contains("Textures/") || value.Contains("Materials/") ||
                   value.Contains("Images/") || value.Contains("Brushes/") ||
                   value.Contains("Prefabs/") || value.Contains("Styles/");
        }

        /// <summary>Quick pre-filter: does this name look like a standalone resource lookup key?</summary>
        private static bool LooksLikeResourceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.Contains('.') || name.Contains('/') || name.Contains('_');
        }

        /// <summary>Determine whether a resource is referenced by any used path.</summary>
        private static bool IsResourceUsed(string childName, string normalizedChildPath,
            HashSet<string> usedPaths)
        {
            foreach (var used in usedPaths)
            {
                var normalizedUsed = NormalizeResourcePath(used);

                // Exact path match or mutual containment
                if (string.Equals(normalizedUsed, normalizedChildPath, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (normalizedUsed.Contains(normalizedChildPath, StringComparison.OrdinalIgnoreCase) ||
                    normalizedChildPath.Contains(normalizedUsed, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Name-only match (last segment of used path vs child name)
                var usedName = System.IO.Path.GetFileName(normalizedUsed);
                if (!string.IsNullOrEmpty(usedName) &&
                    string.Equals(usedName, childName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>True when the item is a folder/library that may contain sub-resources.</summary>
        private static bool IsResourceFolder(object item, string itemType)
        {
            if (string.IsNullOrEmpty(itemType)) return false;
            return itemType.Contains("Library") || itemType.Contains("Folder");
        }

        /// <summary>
        /// True when the type represents an image file resource (not a texture).
        /// "Single Texture", "Cubemap Texture" etc. are textures, not images.
        /// </summary>
        private static bool IsImageResourceType(string childType)
        {
            if (string.IsNullOrEmpty(childType)) return false;
            // "Image" but NOT "Single Texture", "Cubemap Texture", "Texture 2D", etc.
            if (childType.Contains("Texture")) return false;
            return childType.Contains("Image");
        }

        /// <summary>
        /// True when the type represents a texture resource.
        /// Includes "Single Texture", "Cubemap Texture", "Texture 2D" etc.
        /// </summary>
        private static bool IsTextureResourceType(string childType)
        {
            if (string.IsNullOrEmpty(childType)) return false;
            return childType.Contains("Texture");
        }

        /// <summary>
        /// 复制文件到项目的 Images 目录下，返回本地路径
        /// </summary>
        private string? CopyFileToProjectImages(object internalProject, string sourceFilePath)
        {
            try
            {
                // Try to get the project's directory path
                var projectType = internalProject.GetType();
                string? projectDir = null;

                // Try "ProjectDirectory" property
                var projDirProp = projectType.GetProperty("ProjectDirectory",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (projDirProp != null)
                {
                    projectDir = projDirProp.GetValue(internalProject) as string;
                }

                // Fallback: try GetAbsolutePath("")
                if (string.IsNullOrEmpty(projectDir))
                {
                    var getAbsPath = projectType.GetMethod("GetAbsolutePath",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (getAbsPath != null)
                    {
                        try { projectDir = getAbsPath.Invoke(internalProject, new object[] { "" }) as string; }
                        catch { }
                    }
                }

                // Fallback: try "ProjectFilePath" or "FilePath" property
                if (string.IsNullOrEmpty(projectDir))
                {
                    foreach (var propName in new[] { "ProjectFilePath", "FilePath", "ProjectPath", "RootPath", "RootDirectory" })
                    {
                        var prop = projectType.GetProperty(propName,
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (prop != null)
                        {
                            projectDir = prop.GetValue(internalProject) as string;
                            if (!string.IsNullOrEmpty(projectDir))
                            {
                                // If it's a file path, get the directory
                                try { projectDir = System.IO.Path.GetDirectoryName(projectDir); }
                                catch { }
                                break;
                            }
                        }
                    }
                }

                Log($"CopyFileToProjectImages: projectDir={projectDir}");

                if (string.IsNullOrEmpty(projectDir))
                    return null;

                var imagesDir = System.IO.Path.Combine(projectDir, "Images");
                if (!System.IO.Directory.Exists(imagesDir))
                    System.IO.Directory.CreateDirectory(imagesDir);

                var fileName = System.IO.Path.GetFileName(sourceFilePath);
                var destPath = System.IO.Path.Combine(imagesDir, fileName);

                // Don't copy if source is already in the Images directory
                if (string.Equals(sourceFilePath, destPath, StringComparison.OrdinalIgnoreCase))
                    return sourceFilePath;

                System.IO.File.Copy(sourceFilePath, destPath, true);
                Log($"CopyFileToProjectImages: copied to {destPath}");
                return destPath;
            }
            catch (Exception ex)
            {
                Log($"CopyFileToProjectImages error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 尝试通过 ImageDirectory 导入图片文件，返回 ImageFile 对象
        /// </summary>
        private object? TryImportImageFile(object internalProject, string filePath)
        {
            try
            {
                var imgDirProp = internalProject.GetType().GetProperty("ImageDirectory",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (imgDirProp == null)
                {
                    Log($"TryImportImageFile: ImageDirectory property not found");
                    return null;
                }

                var imageDir = imgDirProp.GetValue(internalProject);
                Log($"TryImportImageFile: ImageDirectory={imageDir?.GetType().Name}");

                if (imageDir == null) return null;

                // Diagnostic: log all methods
                var dirMethods = imageDir.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .Where(m => !m.IsSpecialName)
                    .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(",", m.GetParameters().Select(p => $"{p.ParameterType.Name}"))})")
                    .ToList();
                Log($"TryImportImageFile: ImageDirectory methods: {string.Join("; ", dirMethods)}");

                // Try AddNewFile(String) — discovered from API: ObservableFile AddNewFile(String)
                var addNewFileMethod = imageDir.GetType().GetMethod("AddNewFile",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (addNewFileMethod != null)
                {
                    try
                    {
                        Log($"TryImportImageFile: calling AddNewFile({filePath})");
                        var result = addNewFileMethod.Invoke(imageDir, new object[] { filePath });
                        if (result != null)
                        {
                            Log($"TryImportImageFile: AddNewFile succeeded, result={result.GetType().Name}");
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"TryImportImageFile: AddNewFile failed: {ex.Message}");
                        if (ex.InnerException != null)
                            Log($"TryImportImageFile: AddNewFile InnerException: {ex.InnerException.Message}");
                    }
                }

                // Try CreateFile(String,String) — name + filePath
                var createFileMethod = imageDir.GetType().GetMethod("CreateFile",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (createFileMethod != null)
                {
                    try
                    {
                        var fileName = System.IO.Path.GetFileName(filePath);
                        Log($"TryImportImageFile: calling CreateFile({fileName}, {filePath})");
                        createFileMethod.Invoke(imageDir, new object[] { fileName, filePath });
                        // CreateFile returns void, so try to get the created file by name
                        var getChildMethod = imageDir.GetType().GetMethod("GetChildByName",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (getChildMethod != null)
                        {
                            var child = getChildMethod.Invoke(imageDir, new object[] { fileName });
                            if (child != null)
                            {
                                Log($"TryImportImageFile: found created file via GetChildByName: {child.GetType().Name}");
                                return child;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"TryImportImageFile: CreateFile failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"TryImportImageFile error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 设置纹理的 TextureImage 属性（类型为 ImageFile）
        /// </summary>
        private void SetTextureImageProperty(object textureItem, object imageFile)
        {
            try
            {
                var internalTexture = GetInternalProjectItem(textureItem) ?? textureItem;
                var texType = internalTexture.GetType();

                var texImageProp = texType.GetProperty("TextureImage",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (texImageProp != null && texImageProp.CanWrite)
                {
                    try
                    {
                        texImageProp.SetValue(internalTexture, imageFile);
                        Log($"SetTextureImageProperty: set TextureImage successfully");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log($"SetTextureImageProperty: set failed: {ex.Message}");
                    }
                }

                // Fallback: try SetPropertyWithCommand
                var setPropMethod = texType.GetMethod("SetPropertyWithCommand",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (setPropMethod != null)
                {
                    try
                    {
                        setPropMethod.Invoke(internalTexture, new[] { "TextureImage", imageFile });
                        Log($"SetTextureImageProperty: set via SetPropertyWithCommand");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log($"SetTextureImageProperty: SetPropertyWithCommand failed: {ex.Message}");
                    }
                }

                Log($"SetTextureImageProperty: could not set TextureImage");
            }
            catch (Exception ex)
            {
                Log($"SetTextureImageProperty error: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置纹理的源文件路径（fallback when no ImageFile available）
        /// </summary>
        private void SetTextureSourceFile(object textureItem, string filePath)
        {
            try
            {
                var internalTexture = GetInternalProjectItem(textureItem) ?? textureItem;
                var texType = internalTexture.GetType();

                // Try TextureImage first (it's an ImageFile type, not string)
                var texImageProp = texType.GetProperty("TextureImage",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (texImageProp != null && texImageProp.CanWrite)
                {
                    // Try to construct an ImageFile from the file path
                    var imageFileType = texImageProp.PropertyType;
                    var ctor = imageFileType.GetConstructor(
                        BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(string) }, null);
                    if (ctor != null)
                    {
                        try
                        {
                            var imageFile = ctor.Invoke(new object[] { filePath });
                            texImageProp.SetValue(internalTexture, imageFile);
                            Log($"SetTextureSourceFile: created {imageFileType.Name} from path and set TextureImage");
                            return;
                        }
                        catch (Exception ex)
                        {
                            Log($"SetTextureSourceFile: failed to create ImageFile: {ex.Message}");
                        }
                    }
                }
                Log($"SetTextureSourceFile: could not set image source on {texType.Name}");
            }
            catch (Exception ex)
            {
                Log($"SetTextureSourceFile error: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 ImageFile 创建纹理
        /// </summary>
        private object? CreateTextureFromImageFile(object project, object imageFile, string? resourceName)
        {
            try
            {
                // Try to find a method that creates texture from image file
                var texLibProp = project.GetType().GetProperty("TextureLibrary",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (texLibProp == null) return null;

                var texLib = texLibProp.GetValue(project);
                if (texLib == null) return null;

                foreach (var methodName in new[] { "CreateTexture", "AddTexture", "AddNewItem", "CreateItem", "AddChild" })
                {
                    var method = texLib.GetType().GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (method == null) continue;
                    try
                    {
                        var parameters = method.GetParameters();
                        object? result;
                        if (parameters.Length == 1)
                        {
                            result = method.Invoke(texLib, new[] { imageFile });
                        }
                        else if (parameters.Length == 2 && parameters[0].ParameterType.IsAssignableFrom(imageFile.GetType()))
                        {
                            result = method.Invoke(texLib, new[] { imageFile, resourceName ?? "NewTexture" });
                        }
                        else
                        {
                            continue;
                        }
                        if (result != null)
                        {
                            Log($"CreateTextureFromImageFile: {methodName} succeeded");
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateTextureFromImageFile: {methodName} failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"CreateTextureFromImageFile error: {ex.Message}");
            }
            return null;
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
    }
}
