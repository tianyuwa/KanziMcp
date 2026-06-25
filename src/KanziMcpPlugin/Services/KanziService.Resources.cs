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
        /// Import one or many images into the resource library (Textures).
        /// Supports single <c>filePath</c> or batch <c>filePaths</c>.
        /// </summary>
        public string ImportImage(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio not connected");

            if (!args.HasValue)
                return ErrorJson("Missing arguments");

            var resourceName = args.Value.TryGetProperty("resourceName", out var rn) ? rn.GetString() : null;
            var targetFolder = args.Value.TryGetProperty("targetFolder", out var tf) ? tf.GetString() ?? "Textures" : "Textures";

            var filePaths = CollectImportFilePaths(args.Value);
            if (filePaths.Count == 0)
                return ErrorJson("Missing filePath or filePaths parameter");

            Log($"ImportImage: count={filePaths.Count}, targetFolder={targetFolder}");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("No active project");

                var texturesFolder = GetProjectItem(targetFolder) ?? GetOrCreateResourceFolder(project, targetFolder);
                if (texturesFolder == null)
                    return ErrorJson($"Cannot find or create resource folder: {targetFolder}");

                if (filePaths.Count == 1)
                {
                    var single = ImportSingleImage(project, texturesFolder, filePaths[0], resourceName);
                    if (!single.Success)
                        return ErrorJson(single.Error ?? "Import failed");

                    return SafeSerialize(new
                    {
                        success = true,
                        imported = true,
                        path = single.Path,
                        name = single.Name,
                        type = single.Type,
                        sourceFile = single.SourceFile,
                        strategy = single.Strategy
                    });
                }

                var results = new List<Dictionary<string, object?>>();
                var importedCount = 0;
                var failedCount = 0;

                foreach (var path in filePaths)
                {
                    var perFileName = filePaths.Count == 1 ? resourceName : null;
                    var result = ImportSingleImage(project, texturesFolder, path, perFileName);
                    if (result.Success)
                    {
                        importedCount++;
                        results.Add(new Dictionary<string, object?>
                        {
                            ["success"] = true,
                            ["sourceFile"] = result.SourceFile,
                            ["path"] = result.Path,
                            ["name"] = result.Name,
                            ["type"] = result.Type,
                            ["strategy"] = result.Strategy
                        });
                    }
                    else
                    {
                        failedCount++;
                        results.Add(new Dictionary<string, object?>
                        {
                            ["success"] = false,
                            ["sourceFile"] = path,
                            ["error"] = result.Error
                        });
                    }
                }

                return SafeSerialize(new
                {
                    success = failedCount == 0,
                    batch = true,
                    importedCount,
                    failedCount,
                    totalCount = filePaths.Count,
                    results
                });
            }
            catch (Exception ex)
            {
                Log($"ImportImage failed: {ex.Message}");
                return ErrorJson($"Import failed: {ex.Message}");
            }
        }

        private sealed class ImportImageResult
        {
            public bool Success { get; set; }
            public string? Error { get; set; }
            public string? Path { get; set; }
            public string? Name { get; set; }
            public string? Type { get; set; }
            public string? SourceFile { get; set; }
            public string? Strategy { get; set; }
        }

        private static List<string> CollectImportFilePaths(JsonElement args)
        {
            var paths = new List<string>();

            if (args.TryGetProperty("filePaths", out var batchEl) && batchEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in batchEl.EnumerateArray())
                {
                    var path = item.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(path);
                }
            }

            if (paths.Count == 0 &&
                args.TryGetProperty("filePath", out var singleEl) &&
                singleEl.ValueKind == JsonValueKind.String)
            {
                var path = singleEl.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }

            return paths;
        }

        /// <summary>
        /// Unwrap PluginInterface wrappers until Logic.Project (ImageDirectory / DefaultTexture) is reachable.
        /// </summary>
        private object? ResolveLogicProject(object? project)
        {
            if (project == null) return null;

            var visited = new List<object>();
            var current = project;

            for (var depth = 0; depth < 8 && current != null; depth++)
            {
                if (visited.Any(v => ReferenceEquals(v, current)))
                    break;
                visited.Add(current);

                if (HasLogicProjectImportSurface(current))
                {
                    Log($"ResolveLogicProject: using {current.GetType().Name} at depth {depth}");
                    return current;
                }

                var unwrapped = GetInternalProjectItem(current);
                if (unwrapped == null || ReferenceEquals(unwrapped, current))
                    break;
                current = unwrapped;
            }

            return project;
        }

        private static bool HasLogicProjectImportSurface(object project)
        {
            var type = project.GetType();
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            return type.GetProperty("ImageDirectory", bf) != null
                || type.GetProperty("DefaultTexture", bf) != null
                || type.GetProperty("TextureLibrary", bf) != null;
        }

        private object? ResolveTextureParent(object logicProject, object? pluginTexturesFolder, string folderName)
        {
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var texLibProp = logicProject.GetType().GetProperty("TextureLibrary", bf);
            if (texLibProp != null)
            {
                var texLib = texLibProp.GetValue(logicProject);
                if (texLib != null)
                {
                    var internalLib = GetInternalProjectItem(texLib) ?? texLib;
                    Log($"ResolveTextureParent: TextureLibrary={internalLib.GetType().Name}");
                    return internalLib;
                }
            }

            if (pluginTexturesFolder != null)
                return GetInternalProjectItem(pluginTexturesFolder) ?? pluginTexturesFolder;

            var folder = GetProjectItem(folderName);
            return folder != null ? GetInternalProjectItem(folder) ?? folder : null;
        }

        private object? ResolveImageDirectory(object logicProject)
        {
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var imgDirProp = logicProject.GetType().GetProperty("ImageDirectory", bf);
            if (imgDirProp != null)
            {
                var imageDir = imgDirProp.GetValue(logicProject);
                if (imageDir != null)
                    return GetInternalProjectItem(imageDir) ?? imageDir;
            }

            var resDirsProp = logicProject.GetType().GetProperty("ResourceFileDirectories", bf);
            if (resDirsProp?.GetValue(logicProject) is IEnumerable dirs)
            {
                foreach (var dir in dirs)
                {
                    if (dir == null) continue;
                    var typeName = dir.GetType().Name;
                    if (typeName.Contains("ImageDirectory", StringComparison.OrdinalIgnoreCase) ||
                        typeName.Contains("Image", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"ResolveImageDirectory: found via ResourceFileDirectories -> {typeName}");
                        return GetInternalProjectItem(dir) ?? dir;
                    }
                }
            }

            return null;
        }

        private ImportImageResult ImportSingleImage(object project, object texturesFolder,
            string filePath, string? resourceName)
        {
            if (!File.Exists(filePath))
            {
                return new ImportImageResult
                {
                    Success = false,
                    SourceFile = filePath,
                    Error = $"File not found: {filePath}"
                };
            }

            // 路径规范化：E:/... → E:\...，解决正斜杠导致 Kanzi 拼接非法路径
            filePath = Path.GetFullPath(filePath);

            var effectiveName = resourceName ?? Path.GetFileNameWithoutExtension(filePath);

            // 与 Legacy 路径对齐：先将文件复制到项目 Images 目录，避免 Kanzi 拼接外部绝对路径
            var logicProject = ResolveLogicProject(GetInternalProjectItem(project) ?? project) ?? project;
            var localImagePath = CopyFileToProjectImages(logicProject, filePath);
            var importPath = localImagePath ?? filePath;
            if (localImagePath != null)
                Log($"ImportImage: copied to project Images: {localImagePath}");

            // ═══════════════════════════════════════════════════════════
            // SDK 优先路径: Commands.ImportImages（Kanzi PluginInterface SDK）
            // — 使用项目内相对路径，避免 Kanzi 拼接外部绝对路径到 Images 下
            // — ImportImages 第三个参数 false = 不复制（已手动复制到 Images）
            // ═══════════════════════════════════════════════════════════
            var sdkProject = GetSdkProject();
            if (sdkProject != null)
            {
                if (TryImportImagesViaSdk(sdkProject, new[] { importPath }, out var sdkErr))
                {
                    if (TryCompleteSdkImageImport(sdkProject, project, texturesFolder,
                            filePath, effectiveName, out var sdkResult) && sdkResult != null)
                        return sdkResult;

                    Log($"ImportImage: SDK import succeeded but post-processing failed for '{effectiveName}', falling back to legacy reflection");
                }
                else
                {
                    Log($"ImportImage: SDK import failed ({sdkErr?.Message}), falling back to legacy reflection");
                }
            }

            // ═══════════════════════════════════════════════════════════
            // 反射降级路径（现有策略 1-5）
            // ═══════════════════════════════════════════════════════════
            var textureParent = ResolveTextureParent(logicProject, texturesFolder, "Textures");
            Log($"ImportImage: file={importPath}, logicProject={logicProject.GetType().Name}, textureParent={textureParent?.GetType().Name ?? "null"}");
            Log($"ImportImage: local image path for import: {importPath}");

            var imageFile = TryImportImageFile(logicProject, importPath);
            if (imageFile == null && !string.Equals(importPath, filePath, StringComparison.OrdinalIgnoreCase))
                imageFile = TryImportImageFile(logicProject, filePath);

            // Strategy 1: Clone template texture + bind ImageFile (most reliable in Kanzi)
            if (imageFile != null && textureParent != null)
            {
                var fromClone = TryCreateTextureFromImageFile(logicProject, textureParent, texturesFolder,
                    imageFile, effectiveName, importPath, filePath);
                if (fromClone != null)
                {
                    return BuildImportSuccess(fromClone, filePath, "CloneTextureTemplate");
                }
            }

            // Strategy 2: TextureLibrary factory methods
            if (imageFile != null)
            {
                var fromImageFile = CreateTextureFromImageFile(logicProject, imageFile, effectiveName);
                if (fromImageFile != null)
                {
                    return BuildImportSuccess(fromImageFile, filePath, "CreateTextureFromImageFile");
                }
            }

            // Strategy 3: Clone DefaultTexture / library template under TextureLibrary
            if (textureParent != null)
            {
                var cloned = TryCloneDefaultTexture(logicProject, textureParent, texturesFolder, filePath, importPath, imageFile, effectiveName);
                if (cloned != null)
                {
                    return BuildImportSuccess(cloned, filePath, "CloneUnderDefaultTexture");
                }
            }

            // Strategy 4: Kanzi plugin command
            if (textureParent != null)
            {
                var viaCommand = TryImportViaPluginCommand(texturesFolder, importPath, imageFile, effectiveName);
                if (viaCommand != null)
                {
                    return BuildImportSuccess(viaCommand, filePath, "ExecutePluginCommand");
                }
            }

            // Strategy 5: Direct / ImportAsset fallbacks
            var direct = TryCreateTextureDirect(logicProject, textureParent ?? texturesFolder, filePath, importPath, imageFile, effectiveName);
            if (direct != null)
            {
                return BuildImportSuccess(direct, filePath, "DirectTextureCreation");
            }

            return new ImportImageResult
            {
                Success = false,
                SourceFile = filePath,
                Error = $"No suitable import method found for '{Path.GetFileName(filePath)}'. " +
                        "Tried CreateTextureFromImageFile, CloneUnder DefaultTexture, plugin commands, and direct creation."
            };
        }

        private object? TryImportViaPluginCommand(object pluginTexturesFolder, string localImagePath,
            object? imageFile, string resourceName)
        {
            if (_studio == null) return null;

            try
            {
                foreach (var commandName in new[] { "CreateTextureFromImage", "ImportImagesAndCreateTextures", "ImportImages", "CreateSingleTexture" })
                {
                    if (!TryExecuteKanziPluginCommand(commandName, pluginTexturesFolder, "SingleTexture", resourceName, out var created))
                        continue;

                    if (created != null)
                    {
                        if (imageFile != null)
                            SetTextureImageProperty(created, imageFile);
                        else
                            SetTextureSourceFile(created, localImagePath);

                        Log($"ImportImage: plugin command {commandName} succeeded");
                        return created;
                    }

                    var match = GetChildren(pluginTexturesFolder).FirstOrDefault(c =>
                        string.Equals(GetItemName(c), resourceName, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        if (imageFile != null)
                            SetTextureImageProperty(match, imageFile);
                        else
                            SetTextureSourceFile(match, localImagePath);
                        Log($"ImportImage: plugin command {commandName} created {resourceName}");
                        return match;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"TryImportViaPluginCommand failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Create a SingleTexture from an ImageFile by cloning a template texture in TextureLibrary.
        /// </summary>
        private object? TryCreateTextureFromImageFile(object logicProject, object textureParent,
            object pluginTexturesFolder, object imageFile, string resourceName, string localImagePath, string sourceFilePath)
        {
            var existing = FindTextureByName(textureParent, resourceName);
            if (existing != null)
            {
                SetTextureImageProperty(existing, imageFile);
                Log($"TryCreateTextureFromImageFile: updated existing texture {resourceName}");
                return existing;
            }

            var template = FindTextureCloneTemplate(logicProject, textureParent, pluginTexturesFolder);
            if (template == null)
            {
                Log("TryCreateTextureFromImageFile: no texture template available");
                return null;
            }

            Log($"TryCreateTextureFromImageFile: cloning template {GetItemName(template) ?? template.GetType().Name}");
            var cloned = CloneTextureUnder(template, textureParent, resourceName);
            if (cloned == null)
                return null;

            SetTextureImageProperty(cloned, imageFile);
            Log($"TryCreateTextureFromImageFile: created texture {resourceName}");
            return cloned;
        }

        private object? FindTextureByName(object textureParent, string name)
        {
            return GetLibraryItems(textureParent).FirstOrDefault(c =>
                string.Equals(GetItemName(c), name, StringComparison.OrdinalIgnoreCase));
        }

        private object? GetDefaultTextureObject(object host)
        {
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            foreach (var type in new[] { host.GetType() }.Concat(host.GetType().GetInterfaces()))
            {
                var prop = type.GetProperty("DefaultTexture", bf);
                if (prop == null) continue;
                try
                {
                    var val = prop.GetValue(host);
                    if (val != null)
                    {
                        Log($"GetDefaultTextureObject: found on {type.Name} -> {val.GetType().Name}");
                        return GetInternalProjectItem(val) ?? val;
                    }
                }
                catch (Exception ex)
                {
                    Log($"GetDefaultTextureObject: read failed on {type.Name}: {ex.Message}");
                }
            }
            return null;
        }

        private static bool IsLikelyProjectItem(object? value)
        {
            if (value == null) return false;
            var type = value.GetType();
            if (type == typeof(bool) || type == typeof(string) || type.IsValueType) return false;
            var typeName = type.Name;
            return typeName.Contains("ProjectItem")
                || typeName.Contains("Texture")
                || typeName.Contains("Interface");
        }

        private List<object> GetLibraryItems(object libraryOrFolder)
        {
            return GetChildren(libraryOrFolder);
        }

        private object? FindTextureCloneTemplate(object logicProject, object textureParent, object pluginTexturesFolder)
        {
            // TextureLibrary 自带 DefaultTexture 模板（Untitled 项目 Project.DefaultTexture 常为 null）
            var libraryDefault = GetDefaultTextureObject(textureParent);
            if (libraryDefault != null)
            {
                Log("FindTextureCloneTemplate: using TextureLibrary.DefaultTexture");
                return libraryDefault;
            }

            var projectDefault = GetDefaultTextureObject(logicProject);
            if (projectDefault != null)
            {
                Log("FindTextureCloneTemplate: using Project.DefaultTexture");
                return projectDefault;
            }

            foreach (var child in GetLibraryItems(textureParent))
            {
                var typeName = child.GetType().Name;
                var itemType = GetItemType(child) ?? "";
                if (typeName.Contains("SingleTexture", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains("Texture", StringComparison.OrdinalIgnoreCase) ||
                    itemType.Contains("Texture", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"FindTextureCloneTemplate: using existing library texture {GetItemName(child)}");
                    return GetInternalProjectItem(child) ?? child;
                }
            }

            Log("FindTextureCloneTemplate: bootstrapping via CreateSingleTexture");
            return BootstrapTextureTemplate(textureParent, pluginTexturesFolder);
        }

        private object? BootstrapTextureTemplate(object textureParent, object pluginTexturesFolder)
        {
            var before = new HashSet<string>(GetLibraryItems(textureParent).Select(c => GetItemName(c) ?? ""),
                StringComparer.OrdinalIgnoreCase);

            if (TryExecuteKanziPluginCommand("CreateSingleTexture", pluginTexturesFolder, "SingleTexture", "MCP_TemplateTexture", out var created)
                && created != null)
            {
                Log("BootstrapTextureTemplate: CreateSingleTexture returned item");
                return GetInternalProjectItem(created) ?? created;
            }

            var candidate = GetLibraryItems(textureParent)
                .FirstOrDefault(c => !before.Contains(GetItemName(c) ?? ""));
            if (candidate != null)
            {
                Log($"BootstrapTextureTemplate: found new texture {GetItemName(candidate)}");
                return GetInternalProjectItem(candidate) ?? candidate;
            }

            return null;
        }

        private object? CloneTextureUnder(object template, object textureParent, string resourceName)
        {
            try
            {
                var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                var internalTemplate = GetInternalProjectItem(template) ?? template;
                var cloneMethod = internalTemplate.GetType().GetMethod("CloneUnder", bf);
                if (cloneMethod == null)
                {
                    Log("CloneTextureUnder: CloneUnder method not found");
                    return null;
                }

                var pars = cloneMethod.GetParameters();
                if (pars.Length < 3)
                    return null;

                var internalParent = GetInternalProjectItem(textureParent) ?? textureParent;
                var cloneMethodArg = Enum.GetValues(pars[2].ParameterType).GetValue(0);
                return cloneMethod.Invoke(internalTemplate, new[] { resourceName, internalParent, cloneMethodArg });
            }
            catch (Exception ex)
            {
                Log($"CloneTextureUnder failed: {ex.Message}");
                return null;
            }
        }

        private ImportImageResult BuildImportSuccess(object importedItem, string sourceFile, string strategy)
        {
            if (!IsLikelyProjectItem(importedItem))
            {
                return new ImportImageResult
                {
                    Success = false,
                    SourceFile = sourceFile,
                    Error = $"Import produced invalid result type: {importedItem?.GetType().Name ?? "null"}"
                };
            }

            return new ImportImageResult
            {
                Success = true,
                SourceFile = sourceFile,
                Path = GetItemPath(importedItem),
                Name = GetItemName(importedItem),
                Type = GetItemType(importedItem),
                Strategy = strategy
            };
        }

        private object? TryCloneDefaultTexture(object logicProject, object texturesFolder, object pluginTexturesFolder,
            string sourceFilePath, string localImagePath, object? imageFile, string resourceName)
        {
            try
            {
                var template = FindTextureCloneTemplate(logicProject, texturesFolder, pluginTexturesFolder);
                if (template == null)
                    return null;

                var importedItem = CloneTextureUnder(template, texturesFolder, resourceName);
                if (importedItem == null)
                    return null;

                Log($"ImportImage: CloneUnder texture template succeeded for {resourceName}");

                if (imageFile != null)
                    SetTextureImageProperty(importedItem, imageFile);
                else
                    SetTextureSourceFile(importedItem, localImagePath ?? sourceFilePath);

                return importedItem;
            }
            catch (Exception ex)
            {
                Log($"ImportImage: CloneUnder texture template failed: {ex.Message}");
                return null;
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

                // ═══════════════════════════════════════════════════════════
                // SDK 优先路径: project.CreateProjectItem<Asset3DSourceFile>
                //                 + Commands.ImportAsset3DSourceFile
                // ═══════════════════════════════════════════════════════════
                var sdkProject = GetSdkProject();
                if (sdkProject != null &&
                    TryImportFbxViaSdk(sdkProject, filePath, resourceName, out var sdkImported) &&
                    sdkImported != null)
                {
                    Log($"ImportFbx: SDK path succeeded, path={GetItemPath(sdkImported)}");
                    return SafeSerialize(new
                    {
                        success = true,
                        imported = true,
                        path = GetItemPath(sdkImported),
                        name = GetItemName(sdkImported),
                        type = GetItemType(sdkImported),
                        sourceFile = filePath
                    });
                }

                Log("ImportFbx: SDK path failed or not available, falling back to legacy reflection");

                // 使用 Kanzi API 导入 FBX（反射降级路径）
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
            var checkBroken = args.HasValue && args.Value.TryGetProperty("checkBroken", out var cb) && cb.GetBoolean();

            var resourceFolders = new List<string> { "Textures" };
            if (args.HasValue && args.Value.TryGetProperty("resourceFolders", out var foldersEl)
                && foldersEl.ValueKind == JsonValueKind.Array)
            {
                resourceFolders.Clear();
                foreach (var folder in foldersEl.EnumerateArray())
                {
                    var name = folder.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        resourceFolders.Add(name);
                }
                if (resourceFolders.Count == 0)
                    resourceFolders.Add("Textures");
            }

            Log($"DoctorResource: checkImages={checkImages}, checkTextures={checkTextures}, checkBroken={checkBroken}, folders=[{string.Join(", ", resourceFolders)}]");

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("没有打开的项目");

                var unusedImages = new List<Dictionary<string, object>>();
                var unusedTextures = new List<Dictionary<string, object>>();
                var brokenReferences = new List<Dictionary<string, object?>>();
                var scannedFolders = new List<string>();
                var usedResourcePaths = new HashSet<string>();

                // 第一步：收集所有被节点使用的资源路径
                CollectUsedResources(project, usedResourcePaths,
                    out var scannedNodeCount, out var detectedRefCount, out var sampleReferences);

                // 第二步：查找所有 Image 和 Texture 资源
                if (checkImages || checkTextures || checkBroken)
                {
                    foreach (var folderName in resourceFolders)
                    {
                        var folder = GetProjectItem(folderName);
                        if (folder == null)
                            continue;

                        scannedFolders.Add(folderName);

                        if (checkImages || checkTextures)
                            CollectUnusedResources(folder, usedResourcePaths, unusedImages, unusedTextures, 0);

                        if (checkBroken)
                            CollectBrokenTextureReferences(folder, folderName, brokenReferences, 0);
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
                    ["brokenReferences"] = brokenReferences,
                    ["brokenReferenceCount"] = brokenReferences.Count,
                    ["scannedFolders"] = scannedFolders,
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
                if (brokenReferences.Count > 0)
                {
                    recommendations.Add($"Found {brokenReferences.Count} broken texture file references. Fix or re-import affected resources.");
                }
                if (unusedImages.Count == 0 && unusedTextures.Count == 0 && brokenReferences.Count == 0)
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

        /// <summary>
        /// Scan a resource folder tree for texture items whose source files are missing on disk.
        /// </summary>
        private void CollectBrokenTextureReferences(object folder, string parentPath,
            List<Dictionary<string, object?>> broken, int depth)
        {
            if (depth > 30) return;

            try
            {
                foreach (var child in GetChildren(folder))
                {
                    var name = GetItemName(child);
                    var type = GetItemType(child);
                    var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

                    if (type.Contains("Texture"))
                    {
                        try
                        {
                            var filePath = SafeGetProperty(child, "FilePath") as string
                                ?? SafeGetProperty(child, "Source") as string
                                ?? SafeGetProperty(child, "Image") as string;

                            if (!string.IsNullOrEmpty(filePath) && !File.Exists(filePath))
                            {
                                var projectDir = Path.GetDirectoryName(
                                    SafeGetProperty(GetActiveProject(), "FullPath") as string ?? "");
                                var fullPath = string.IsNullOrEmpty(projectDir)
                                    ? filePath
                                    : Path.Combine(projectDir, filePath);

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

                    if (IsResourceFolder(child, type))
                        CollectBrokenTextureReferences(child, path, broken, depth + 1);
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
                var imageDir = ResolveImageDirectory(internalProject);
                if (imageDir == null)
                {
                    Log($"TryImportImageFile: ImageDirectory not found on {internalProject.GetType().Name}");
                    return null;
                }

                Log($"TryImportImageFile: ImageDirectory={imageDir.GetType().Name}");

                // Diagnostic: log all methods
                var dirMethods = imageDir.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .Where(m => !m.IsSpecialName)
                    .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(",", m.GetParameters().Select(p => $"{p.ParameterType.Name}"))})")
                    .ToList();
                Log($"TryImportImageFile: ImageDirectory methods: {string.Join("; ", dirMethods)}");

                foreach (var candidatePath in new[] { filePath }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
                        continue;
                    var pathToUse = candidatePath;
                    // Try AddNewFile(String) — discovered from API: ObservableFile AddNewFile(String)
                    var addNewFileMethod = imageDir.GetType().GetMethod("AddNewFile",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (addNewFileMethod != null)
                    {
                        try
                        {
                            Log($"TryImportImageFile: calling AddNewFile({pathToUse})");
                            var result = addNewFileMethod.Invoke(imageDir, new object[] { pathToUse });
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
                            var fileName = Path.GetFileName(pathToUse);
                            Log($"TryImportImageFile: calling CreateFile({fileName}, {pathToUse})");
                            createFileMethod.Invoke(imageDir, new object[] { fileName, pathToUse });

                            foreach (var lookupName in new[] { fileName, Path.GetFileNameWithoutExtension(fileName) })
                            {
                                var getChildMethod = imageDir.GetType().GetMethod("GetChildByName",
                                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                                if (getChildMethod == null) continue;

                                var child = getChildMethod.Invoke(imageDir, new object[] { lookupName });
                                if (child != null)
                                {
                                    Log($"TryImportImageFile: found created file via GetChildByName({lookupName}): {child.GetType().Name}");
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
                var internalImageFile = GetInternalProjectItem(imageFile) ?? imageFile;
                var texType = internalTexture.GetType();

                var texImageProp = texType.GetProperty("TextureImage",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (texImageProp != null && texImageProp.CanWrite)
                {
                    try
                    {
                        texImageProp.SetValue(internalTexture, internalImageFile);
                        Log($"SetTextureImageProperty: set TextureImage successfully");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log($"SetTextureImageProperty: direct set failed: {ex.Message}, trying assignable match...");
                        if (TrySetPropertyValueCompatible(internalTexture, texImageProp, internalImageFile))
                            return;
                    }
                }

                // Fallback: try SetPropertyWithCommand
                var setPropMethod = texType.GetMethod("SetPropertyWithCommand",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (setPropMethod != null)
                {
                    try
                    {
                        setPropMethod.Invoke(internalTexture, new[] { "TextureImage", internalImageFile });
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

        private static bool TrySetPropertyValueCompatible(object target, PropertyInfo property, object value)
        {
            if (property.PropertyType.IsInstanceOfType(value))
            {
                property.SetValue(target, value);
                return true;
            }
            return false;
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
        /// Fallback: create texture directly via CreateProjectItem API, bypassing CloneUnder.
        /// </summary>
        private object? TryCreateTextureDirect(object internalProject, object texturesFolder,
            string sourceFilePath, string? localImagePath, object? imageFile, string? resourceName)
        {
            try
            {
                var name = resourceName ?? System.IO.Path.GetFileNameWithoutExtension(sourceFilePath);
                var internalParent = GetInternalProjectItem(texturesFolder) ?? texturesFolder;
                var projectType = internalProject.GetType();

                Log($"TryCreateTextureDirect: name={name}, source={sourceFilePath}, localImage={localImagePath}");

                // Strategy 1: CreateProjectItem<SingleTexture> or <Texture> via generic method
                foreach (var typeName in new[] { "SingleTexture", "Texture", "Texture2D", "Texture 2D" })
                {
                    try
                    {
                        var createMethod = projectType.GetMethod("CreateProjectItem",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (createMethod != null && createMethod.IsGenericMethodDefinition)
                        {
                            var texType = FindTypeInAssemblies(typeName);
                            if (texType != null)
                            {
                                var genericMethod = createMethod.MakeGenericMethod(texType);
                                var result = genericMethod.Invoke(internalProject, new[] { name, internalParent });
                                if (result != null)
                                {
                                    Log($"TryCreateTextureDirect: created via CreateProjectItem<{typeName}>");

                                    // Set source file
                                    if (imageFile != null)
                                        SetTextureImageProperty(result, imageFile);
                                    else
                                        SetTextureSourceFile(result, sourceFilePath);

                                    return result;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"TryCreateTextureDirect: CreateProjectItem<{typeName}> failed: {ex.Message}");
                    }
                }

                // Strategy 2: Try ImportAsset on project
                try
                {
                    var importAssetMethod = projectType.GetMethod("ImportAsset",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (importAssetMethod != null)
                    {
                        var result = importAssetMethod.Invoke(internalProject, new object[] { sourceFilePath, "Textures" });
                        if (result != null)
                        {
                            Log($"TryCreateTextureDirect: created via ImportAsset");
                            return result;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"TryCreateTextureDirect: ImportAsset failed: {ex.Message}");
                }

                // Strategy 3: Try TextureLibrary.AddNewItem / CreateItem
                try
                {
                    var texLibProp = projectType.GetProperty("TextureLibrary",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (texLibProp != null)
                    {
                        var texLib = texLibProp.GetValue(internalProject);
                        if (texLib != null)
                        {
                            foreach (var methodName in new[] { "AddNewItem", "CreateItem", "CreateTexture", "AddTexture" })
                            {
                                try
                                {
                                    var method = texLib.GetType().GetMethod(methodName,
                                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                                    if (method != null)
                                    {
                                        var pars = method.GetParameters();
                                        object? tex;
                                        if (pars.Length == 2 && pars[0].ParameterType == typeof(string))
                                            tex = method.Invoke(texLib, new object[] { name, sourceFilePath });
                                        else if (pars.Length == 1 && pars[0].ParameterType == typeof(string))
                                            tex = method.Invoke(texLib, new object[] { sourceFilePath });
                                        else
                                            continue;

                                        if (tex != null)
                                        {
                                            Log($"TryCreateTextureDirect: created via TextureLibrary.{methodName}");
                                            return tex;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"TryCreateTextureDirect: TextureLibrary strategy failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"TryCreateTextureDirect error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 从 ImageFile 创建纹理
        /// </summary>
        private object? CreateTextureFromImageFile(object project, object imageFile, string? resourceName)
        {
            try
            {
                var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                var texLibProp = project.GetType().GetProperty("TextureLibrary", bf);
                if (texLibProp == null)
                {
                    Log("CreateTextureFromImageFile: TextureLibrary property not found");
                    return null;
                }

                var texLib = texLibProp.GetValue(project);
                if (texLib == null)
                {
                    Log("CreateTextureFromImageFile: TextureLibrary is null");
                    return null;
                }

                var internalTexLib = GetInternalProjectItem(texLib) ?? texLib;
                var internalImageFile = GetInternalProjectItem(imageFile) ?? imageFile;
                var name = resourceName ?? "NewTexture";

                foreach (var methodName in new[] { "CreateTexture", "AddTexture", "AddNewItem", "CreateItem", "AddChild", "CloneUnder" })
                {
                    var method = internalTexLib.GetType().GetMethod(methodName, bf);
                    if (method == null) continue;
                    try
                    {
                        var parameters = method.GetParameters();
                        object? result = null;

                        if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string)
                            && parameters[1].ParameterType.IsInstanceOfType(internalImageFile))
                            result = method.Invoke(internalTexLib, new[] { name, internalImageFile });
                        else if (parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(internalImageFile))
                            result = method.Invoke(internalTexLib, new[] { internalImageFile });
                        else if (parameters.Length == 2 && parameters[0].ParameterType.IsInstanceOfType(internalImageFile)
                            && parameters[1].ParameterType == typeof(string))
                            result = method.Invoke(internalTexLib, new[] { internalImageFile, name });
                        else
                            continue;

                        if (IsLikelyProjectItem(result))
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
