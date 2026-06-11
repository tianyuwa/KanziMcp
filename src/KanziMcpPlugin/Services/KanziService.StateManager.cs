// KanziService.StateManager.cs — 状态机创建
//
// 文件作用: 创建 StateManager + StateGroup + States + StateObjects，支持分批执行
// 关键方法: CreateStateManager
// 参考: createStateManager\UserControl1.xaml.cs (createStateManager, createState, createStateObject,
//        addProjectItemProperty, SetProjectItemProperty)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Rightware.Kanzi.Studio.PluginInterface;

namespace KanziMcpPlugin.Services
{
    public partial class KanziService
    {
        #region State Manager 创建

        private const int RecommendedBatchSize = 8;

        /// <summary>
        /// 创建 State Manager（分批支持，性能协议）
        /// </summary>
        public string CreateStateManager(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio not connected");

            if (!args.HasValue)
                return ErrorJson("Missing arguments");

            // Parse parameters
            var managerName = GetStringArg(args.Value, "managerName");
            var groupName = GetStringArg(args.Value, "groupName");
            var groupProperty = GetStringArg(args.Value, "groupProperty");
            var bindNodePath = GetStringArg(args.Value, "bindNodePath") ?? "";
            var mode = GetStringArg(args.Value, "mode") ?? "preview";
            var confirmLargeBatch = GetBoolArg(args.Value, "confirmLargeBatch");
            var batchIndex = GetIntArg(args.Value, "batchIndex", 0);
            var batchSize = GetIntArg(args.Value, "batchSize", RecommendedBatchSize);
            var totalStateCountArg = GetIntArg(args.Value, "totalStateCount", 0);
            var autoGenerateCount = GetIntArg(args.Value, "autoGenerateCount", 0);
            var strategy = GetStringArg(args.Value, "strategy") ?? "auto";

            if (string.IsNullOrEmpty(managerName) || string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(groupProperty))
                return ErrorJson("Missing required parameters: managerName, groupName, groupProperty");

            // Clamp batchSize
            if (batchSize < 1) batchSize = 1;
            if (batchSize > 100) batchSize = 100;

            // Parse states
            var allStates = ParseStateDefinitions(args.Value);

            // Auto-generate states from template if autoGenerateCount is set
            if (autoGenerateCount > 0 && allStates != null && allStates.Count > 0)
            {
                var template = allStates[0];
                allStates = GenerateStatesFromTemplate(template, autoGenerateCount);
                Log($"CreateStateManager: auto-generated {allStates.Count} states from template");
            }

            if (allStates == null || allStates.Count == 0)
                return ErrorJson("No states provided or states array is invalid");

            var stateCount = allStates.Count;

            // 9+ states: enforce small batches on apply to avoid TCP/MCP timeouts
            if (stateCount >= 9 && batchSize > RecommendedBatchSize)
            {
                if (mode == "apply")
                {
                    Log($"CreateStateManager: clamping batchSize {batchSize} -> {RecommendedBatchSize} (stateCount={stateCount}, mode=apply)");
                    batchSize = RecommendedBatchSize;
                }
            }

            var totalBatches = (int)Math.Ceiling((double)stateCount / batchSize);
            if (totalBatches == 0) totalBatches = 1;

            // Count state objects and property writes
            int stateObjectCount = 0;
            int propertyWriteCount = 0;
            foreach (var s in allStates)
            {
                var objs = s.Objects;
                if (objs != null)
                {
                    stateObjectCount += objs.Count;
                    foreach (var o in objs)
                    {
                        if (o.Properties != null)
                            propertyWriteCount += o.Properties.Count;
                    }
                }
            }

            Log($"CreateStateManager: {stateCount} states, {stateObjectCount} objects, {propertyWriteCount} props, batch={batchIndex}/{totalBatches}, mode={mode}");

            // === Scale guards ===
            if (stateCount > 500)
            {
                return ErrorJson(
                    $"State count ({stateCount}) exceeds maximum of 500 per StateGroup. " +
                    $"Please split into multiple StateGroups or use Data Source instead.");
            }

            if (stateCount > 200 && !confirmLargeBatch && mode == "apply")
            {
                return ErrorJson(
                    $"State count ({stateCount}) > 200 requires confirmLargeBatch: true. " +
                    $"Set confirmLargeBatch: true to proceed, or use a smaller batch.");
            }

            // === Validate groupProperty ===
            string? enumValidationError;
            var enumOptions = ValidateGroupProperty(groupProperty, allStates, out enumValidationError);
            if (enumValidationError != null)
                return ErrorJson(enumValidationError);

            // === PREVIEW mode ===
            if (mode == "preview")
            {
                var riskLevel = stateCount <= 8 ? "low" :
                               stateCount <= 50 ? "medium" :
                               stateCount <= 200 ? "medium" : "high";

                var recommendation = stateCount <= 8 ? "single_apply" :
                                    stateCount >= 9 ? "batch_required" :
                                    stateCount <= 200 ? "batch_recommended" : "batch_required";

                var effectiveBatchSize = stateCount >= 9
                    ? Math.Min(batchSize, RecommendedBatchSize)
                    : batchSize;
                var previewGrandTotal = totalStateCountArg > 0 ? totalStateCountArg : stateCount;
                var effectiveTotalBatches = (int)Math.Ceiling((double)previewGrandTotal / effectiveBatchSize);

                // Validate bindNodePath exists
                bool bindNodeExists = false;
                if (!string.IsNullOrEmpty(bindNodePath))
                {
                    try
                    {
                        bindNodeExists = _studio!.ActiveProject.GetProjectItem(bindNodePath) != null;
                    }
                    catch { }
                }

                var batchPlan = new Dictionary<string, object>
                {
                    ["stateCount"] = stateCount,
                    ["stateObjectCount"] = stateObjectCount,
                    ["propertyWriteCount"] = propertyWriteCount,
                    ["batchSize"] = batchSize,
                    ["totalStateCount"] = totalStateCountArg > 0 ? totalStateCountArg : (object?)null,
                    ["recommendedBatchSize"] = stateCount >= 9 ? RecommendedBatchSize : batchSize,
                    ["effectiveBatchSize"] = effectiveBatchSize,
                    ["totalBatches"] = totalBatches,
                    ["effectiveTotalBatches"] = effectiveTotalBatches,
                    ["estimatedTimeMs"] = EstimateTimeMs(stateCount, effectiveBatchSize),
                    ["riskLevel"] = riskLevel,
                    ["recommendation"] = recommendation,
                    ["applyHint"] = previewGrandTotal >= 9
                        ? $"Send the FULL states array on every apply call with batchSize={RecommendedBatchSize}, loop batchIndex=0..{effectiveTotalBatches - 1}. " +
                          $"Alternatively send only each batch's states with totalStateCount={previewGrandTotal}."
                        : "Single apply is OK for <= 8 states",
                    ["bindNodePath"] = bindNodePath,
                    ["bindNodeExists"] = bindNodeExists,
                    ["groupProperty"] = groupProperty,
                    ["groupPropertyValid"] = true,
                    ["enumOptions"] = enumOptions?.Select(o => new { name = o.Key, value = o.Value }).ToList()
                };

                return SafeSerialize(new
                {
                    preview = true,
                    managerName,
                    groupName,
                    stateCount,
                    totalBatches = effectiveTotalBatches,
                    batchPlan
                });
            }

            // === APPLY mode ===
            var sw = Stopwatch.StartNew();

            try
            {
                var payloadCount = allStates.Count;
                var batchResolution = ResolveApplyBatch(
                    allStates, batchIndex, batchSize, totalStateCountArg);

                if (batchResolution.error != null)
                    return ErrorJson(batchResolution.error);

                var batchStates = batchResolution.batchStates;
                var startIdx = batchResolution.startIdx;
                var endIdx = batchResolution.endIdx;
                var grandTotal = batchResolution.grandTotal;
                var applyTotalBatches = batchResolution.totalBatches;
                var isLastBatch = batchResolution.isLastBatch;
                var partialPayload = batchResolution.partialPayload;

                Log($"CreateStateManager: apply batch {batchIndex}, states [{startIdx}..{endIdx}), " +
                    $"payload={payloadCount}, grandTotal={grandTotal}, partial={partialPayload}");

                // 使用 PluginInterface 强类型 API（与 createStateManager 参考插件一致）
                var pluginProject = _studio!.ActiveProject;
                Log($"CreateStateManager: using PluginInterface Project API");

                WriteProgress($"StateManager starting: {startIdx}/{grandTotal}");

                ProjectItem? stateGroupItem = null;
                ProjectItem? templateStateItem = null;
                bool isFirstBatch = (batchIndex == 0);

                if (isFirstBatch)
                {
                    string fullPath = $"State Managers/{managerName}/{groupName}";
                    if (pluginProject.GetProjectItem(fullPath) != null)
                        return ErrorJson($"State Manager group already exists at '{fullPath}'. Cannot overwrite.");

                    string managerPath = $"State Managers/{managerName}";
                    ProjectItem managerItem = pluginProject.GetProjectItem(managerPath);
                    if (managerItem == null)
                    {
                        managerItem = pluginProject.CreateProjectItem<StateManager>(
                            managerName, pluginProject.StateManagerLibrary);
                        Log($"CreateStateManager: created StateManager '{managerName}'");
                    }
                    else
                    {
                        Log($"CreateStateManager: using existing StateManager at '{managerPath}'");
                    }

                    if (managerItem == null)
                        return ErrorJson("Failed to create or find StateManager");

                    stateGroupItem = pluginProject.CreateProjectItem<StateGroup>(groupName, managerItem);
                    if (stateGroupItem == null)
                        return ErrorJson("Failed to create StateGroup");

                    SetProjectItemPropertyTyped(stateGroupItem, "StateGroupControllerPropertyTypeReference", groupProperty);
                    Log($"CreateStateManager: created StateGroup '{groupName}'");

                    var result = CreateStatesInBatchTyped(pluginProject, stateGroupItem, batchStates, groupProperty,
                        strategy, ref templateStateItem);

                    if (!result.success)
                        return ErrorJson(result.error ?? "Failed to create states");

                    WriteProgress($"StateManager batch {batchIndex}: {endIdx}/{grandTotal}");
                }
                else
                {
                    string smPath = $"State Managers/{managerName}/{groupName}";
                    stateGroupItem = pluginProject.GetProjectItem(smPath);

                    if (stateGroupItem == null)
                        return ErrorJson($"StateGroup not found at '{smPath}'. Run batchIndex=0 first.");

                    foreach (var child in stateGroupItem.Children)
                    {
                        templateStateItem = child;
                        Log($"CreateStateManager: using existing State[0] '{child.Name}' as template for clone");
                        break;
                    }

                    var result = CreateStatesInBatchTyped(pluginProject, stateGroupItem, batchStates, groupProperty,
                        strategy, ref templateStateItem);

                    if (!result.success)
                        return ErrorJson(result.error ?? "Failed to create states in batch");

                    WriteProgress($"StateManager batch {batchIndex}: {endIdx}/{grandTotal}");
                }

                if (isLastBatch && !string.IsNullOrEmpty(bindNodePath))
                {
                    try
                    {
                        var bindNode = pluginProject.GetProjectItem(bindNodePath);
                        var smItem = pluginProject.GetProjectItem($"State Managers/{managerName}");
                        if (bindNode != null && smItem != null)
                        {
                            SetProjectItemPropertyTyped(bindNode, "Node.StateManager", smItem);
                            Log($"CreateStateManager: bound Node.StateManager to 'State Managers/{managerName}'");
                        }
                        else
                        {
                            Log($"CreateStateManager: bindNodePath '{bindNodePath}' or StateManager not found, skipping bind");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CreateStateManager: bindNodePath failed: {ex.Message}");
                    }
                }

                sw.Stop();
                WriteProgress($"StateManager completed: {endIdx}/{grandTotal}, elapsed={sw.Elapsed.TotalSeconds:F1}s");

                return SafeSerialize(new
                {
                    success = true,
                    preview = false,
                    managerName,
                    groupName,
                    batchIndex,
                    completedStates = endIdx,
                    batchStatesCreated = batchStates.Count,
                    totalBatches = applyTotalBatches,
                    totalStates = grandTotal,
                    partialPayload,
                    elapsedMs = sw.ElapsedMilliseconds,
                    hasMore = !isLastBatch,
                    bindNodePath = isLastBatch ? bindNodePath : null
                });
            }
            catch (Exception ex)
            {
                Log($"CreateStateManager failed: {ex.Message}");
                return ErrorJson($"CreateStateManager failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 使用 PluginInterface 强类型 API 创建一批 State（参考 createStateManager 插件）
        /// </summary>
        private (bool success, string? error) CreateStatesInBatchTyped(Project project, ProjectItem stateGroupItem,
            List<StateDefinition> batchStates, string groupProperty, string strategy,
            ref ProjectItem? templateState)
        {
            try
            {
                for (int i = 0; i < batchStates.Count; i++)
                {
                    var sd = batchStates[i];
                    ProjectItem? stateItem;
                    bool clonedSuccessfully = false;

                    bool useClone = (strategy == "clone") ||
                                    (strategy == "auto" && templateState != null);

                    if (useClone && templateState != null)
                    {
                        stateItem = CloneStateTyped(templateState, sd.StateName);
                        if (stateItem != null)
                        {
                            clonedSuccessfully = true;
                            SetProjectItemPropertyTyped(stateItem, groupProperty, sd.StatePropertyValue);
                        }
                        else
                        {
                            Log($"CreateStateManager: clone failed for '{sd.StateName}', falling back to direct create");
                            stateItem = CreateStateTyped(project, stateGroupItem, sd.StateName, groupProperty, sd.StatePropertyValue);
                        }
                    }
                    else
                    {
                        stateItem = CreateStateTyped(project, stateGroupItem, sd.StateName, groupProperty, sd.StatePropertyValue);
                    }

                    if (stateItem == null)
                        return (false, $"Failed to create state '{sd.StateName}'");

                    if (templateState == null)
                        templateState = stateItem;

                    if (sd.Objects != null && sd.Objects.Count > 0)
                    {
                        for (int oi = 0; oi < sd.Objects.Count; oi++)
                        {
                            var objDef = sd.Objects[oi];
                            if (objDef.Properties == null || objDef.Properties.Count == 0)
                                continue;

                            ProjectItem? so = clonedSuccessfully
                                ? FindClonedStateObjectTyped(stateItem, objDef, oi)
                                : null;

                            if (so != null && !string.IsNullOrEmpty(objDef.NodePath))
                                UpdateStateObjectTargetPath(so, objDef.NodePath);

                            if (so == null)
                            {
                                so = CreateStateObjectTyped(project, stateItem, objDef.NodeName, objDef.NodePath);
                                if (so == null)
                                {
                                    Log($"CreateStateManager: failed to create StateObject '{objDef.NodeName}'");
                                    continue;
                                }
                            }

                            foreach (var kvp in objDef.Properties)
                                SetProjectItemPropertyTyped(so, kvp.Key, kvp.Value);
                        }
                    }

                    if ((i + 1) % 10 == 0)
                        WriteProgress($"StateManager progress: {i + 1}/{batchStates.Count}");

                    // Pump WPF messages to prevent UI thread blocking and Kanzi API timeouts
                    PumpWpfMessages();
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private ProjectItem? CreateStateTyped(Project project, ProjectItem parent, string name,
            string propertyName, object propertyValue)
        {
            try
            {
                var state = project.CreateProjectItem<State>(name, parent);
                if (state != null)
                    SetProjectItemPropertyTyped(state, propertyName, propertyValue);
                return state;
            }
            catch (Exception ex)
            {
                Log($"CreateStateTyped: {ex.Message}");
                if (ex.InnerException != null)
                    Log($"  InnerException: {ex.InnerException.Message}");
                return null;
            }
        }

        private ProjectItem? CreateStateObjectTyped(Project project, ProjectItem parent, string name, string objectPath)
        {
            try
            {
                var stateObject = project.CreateProjectItem<StateObject>(name, parent);
                if (stateObject != null)
                    stateObject.TargetObjectPath = objectPath;
                return stateObject;
            }
            catch (Exception ex)
            {
                Log($"CreateStateObjectTyped: {ex.Message}");
                if (ex.InnerException != null)
                    Log($"  InnerException: {ex.InnerException.Message}");
                return null;
            }
        }

        private ProjectItem? CloneStateTyped(ProjectItem templateState, string newName)
        {
            try
            {
                var cloneUnderMethod = templateState.GetType().GetMethod("CloneUnder",
                    BindingFlags.Public | BindingFlags.Instance);
                if (cloneUnderMethod == null)
                {
                    Log("CloneStateTyped: CloneUnder method not found");
                    return null;
                }

                var pars = cloneUnderMethod.GetParameters();
                if (pars.Length < 3)
                    return null;

                var parent = templateState.Parent;
                if (parent == null)
                    return null;

                var cloneMethodType = pars[2].ParameterType;
                var defaultValue = Enum.GetValues(cloneMethodType).GetValue(0);
                var result = cloneUnderMethod.Invoke(templateState, new[] { newName, parent, defaultValue }) as ProjectItem;
                Log($"CloneStateTyped: cloned '{templateState.Name}' -> '{newName}'");
                return result;
            }
            catch (Exception ex)
            {
                Log($"CloneStateTyped failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reuse StateObject children copied by CloneUnder instead of creating duplicates.
        /// </summary>
        private static ProjectItem? FindClonedStateObjectTyped(
            ProjectItem stateItem, StateObjectDefinition objDef, int objectIndex)
        {
            var stateObjects = CollectStateObjectChildren(stateItem);
            if (stateObjects.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(objDef.NodeName))
            {
                var byName = stateObjects.FirstOrDefault(c =>
                    string.Equals(c.Name, objDef.NodeName, StringComparison.Ordinal));
                if (byName != null)
                    return byName;
            }

            if (!string.IsNullOrEmpty(objDef.NodePath))
            {
                foreach (var child in stateObjects)
                {
                    if (child is StateObject typed && typed.TargetObjectPath == objDef.NodePath)
                        return child;

                    try
                    {
                        var pathProp = child.GetType().GetProperty("TargetObjectPath");
                        if (pathProp?.GetValue(child) as string == objDef.NodePath)
                            return child;
                    }
                    catch { }
                }
            }

            return objectIndex < stateObjects.Count ? stateObjects[objectIndex] : null;
        }

        private static List<ProjectItem> CollectStateObjectChildren(ProjectItem stateItem)
        {
            var result = new List<ProjectItem>();
            foreach (var child in stateItem.Children)
            {
                if (child is StateObject || child.GetType().Name.IndexOf("StateObject", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Add(child);
            }
            return result;
        }

        private static void UpdateStateObjectTargetPath(ProjectItem stateObject, string nodePath)
        {
            try
            {
                if (stateObject is StateObject typed)
                {
                    typed.TargetObjectPath = nodePath;
                    return;
                }

                var pathProp = stateObject.GetType().GetProperty("TargetObjectPath");
                pathProp?.SetValue(stateObject, nodePath);
            }
            catch
            {
                // Best-effort path sync on cloned objects
            }
        }

        private void SetProjectItemPropertyTyped(ProjectItem item, string propertyName, object propertyValue)
        {
            object typedValue = InferPropertyValue(propertyValue);

            if (!item.HasProperty(propertyName))
            {
                try
                {
                    item.AddProperty(propertyName);
                }
                catch (Exception ex)
                {
                    Log($"SetProjectItemPropertyTyped: AddProperty '{propertyName}' failed: {ex.Message}");
                }
            }

            try
            {
                item.Set(propertyName, typedValue);
            }
            catch (Exception ex)
            {
                Log($"SetProjectItemPropertyTyped: Set '{propertyName}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Create states in the current batch
        /// </summary>
        private (bool success, string? error) CreateStatesInBatch(object project, object stateGroupObj,
            List<StateDefinition> batchStates, string groupProperty, string strategy,
            ref object? templateState)
        {
            try
            {
                for (int i = 0; i < batchStates.Count; i++)
                {
                    var sd = batchStates[i];

                    object? stateObj;

                    // Determine clone vs direct strategy
                    bool useClone = (strategy == "clone") ||
                                    (strategy == "auto" && templateState != null);

                    if (useClone && templateState != null)
                    {
                        stateObj = CloneStateInternal(templateState, sd.StateName);
                        if (stateObj == null)
                        {
                            Log($"CreateStateManager: clone failed for '{sd.StateName}', falling back to direct create");
                            stateObj = CreateStateDirect(project, stateGroupObj, sd.StateName, groupProperty, sd.StatePropertyValue);
                        }
                    }
                    else
                    {
                        stateObj = CreateStateDirect(project, stateGroupObj, sd.StateName, groupProperty, sd.StatePropertyValue);
                    }

                    if (stateObj == null)
                        return (false, $"Failed to create state '{sd.StateName}'");

                    // Store first state as template
                    if (templateState == null)
                        templateState = stateObj;

                    // Create StateObjects
                    if (sd.Objects != null && sd.Objects.Count > 0)
                    {
                        foreach (var objDef in sd.Objects)
                        {
                            if (objDef.Properties == null || objDef.Properties.Count == 0)
                                continue;

                            var so = CreateStateObjectDirect(project, stateObj, objDef.NodeName, objDef.NodePath);
                            if (so == null)
                            {
                                Log($"CreateStateManager: failed to create StateObject '{objDef.NodeName}'");
                                continue;
                            }

                            // Set properties
                            foreach (var kvp in objDef.Properties)
                            {
                                TrySetPropertyValue(so, kvp.Key, kvp.Value);
                            }
                        }
                    }

                    // Progress log every 10 states
                    if ((i + 1) % 10 == 0)
                    {
                        WriteProgress($"StateManager progress: {i + 1}/{batchStates.Count}");
                    }
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Create a State directly (no clone)
        /// </summary>
        private object? CreateStateDirect(object project, object parent, string name,
            string propertyName, object propertyValue)
        {
            try
            {
                var state = CreateProjectItemReflection(project, parent, name, "State");
                if (state != null)
                {
                    TrySetProperty(state, propertyName, propertyValue);
                }
                return state;
            }
            catch (Exception ex)
            {
                Log($"CreateStateDirect: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create a StateObject directly
        /// </summary>
        private object? CreateStateObjectDirect(object project, object parent, string name, string objectPath)
        {
            try
            {
                var stateObject = CreateProjectItemReflection(project, parent, name, "StateObject");
                if (stateObject != null)
                {
                    TrySetProperty(stateObject, "TargetObjectPath", objectPath);
                }
                return stateObject;
            }
            catch (Exception ex)
            {
                Log($"CreateStateObjectDirect: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clone a State and rename it (CloneUnder reflection)
        /// </summary>
        private object? CloneStateInternal(object templateState, string newName)
        {
            try
            {
                var internalTemplate = GetInternalProjectItem(templateState);
                if (internalTemplate == null)
                {
                    Log("CloneStateInternal: GetInternalProjectItem returned null");
                    return null;
                }

                var cloneUnderMethod = internalTemplate.GetType().GetMethod("CloneUnder",
                    BindingFlags.Public | BindingFlags.Instance);
                if (cloneUnderMethod == null)
                {
                    Log("CloneStateInternal: CloneUnder method not found");
                    return null;
                }

                var pars = cloneUnderMethod.GetParameters();
                if (pars.Length < 3)
                {
                    Log($"CloneStateInternal: CloneUnder has {pars.Length} params, expected 3+");
                    return null;
                }

                // Get parent from template
                var parent = SafeGetProperty(templateState, "Parent");
                if (parent == null)
                {
                    Log("CloneStateInternal: cannot get parent of template state");
                    return null;
                }

                var cloneMethodType = pars[2].ParameterType;
                var defaultValue = Enum.GetValues(cloneMethodType).GetValue(0);

                var result = cloneUnderMethod.Invoke(internalTemplate,
                    new[] { newName, parent, defaultValue });

                Log($"CloneStateInternal: cloned '{GetItemName(templateState)}' -> '{newName}'");
                return result;
            }
            catch (Exception ex)
            {
                Log($"CloneStateInternal failed: {ex.Message}");
                if (ex.InnerException != null)
                    Log($"  InnerException: {ex.InnerException.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create a project item using reflection (generic CreateProjectItem)
        /// </summary>
        private object? CreateProjectItemReflection(object project, object parent, string name, string typeName)
        {
            try
            {
                // Try generic CreateProjectItem<T>(string, ProjectItem)
                var createMethod = project.GetType().GetMethod("CreateProjectItem",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (createMethod != null && createMethod.IsGenericMethodDefinition)
                {
                    // Try with the given typeName
                    var type = FindTypeInAssemblies(typeName);
                    if (type != null)
                    {
                        try
                        {
                            var genericMethod = createMethod.MakeGenericMethod(type);
                            var result = genericMethod.Invoke(project, new[] { name, parent });
                            if (result != null)
                            {
                                Log($"CreateProjectItemReflection: CreateProjectItem<{typeName}> succeeded for '{name}'");
                                return result;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"CreateProjectItemReflection: CreateProjectItem<{typeName}> failed: {ex.Message}");
                        }
                    }
                }

                // Fallback: try non-generic CreateChild or AddChild on parent
                var createChildMethod = parent.GetType().GetMethod("CreateChild",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (createChildMethod != null)
                {
                    try
                    {
                        var child = createChildMethod.Invoke(parent, new object[] { name, typeName });
                        if (child != null) return child;
                    }
                    catch { }
                }

                Log($"CreateProjectItemReflection: all strategies failed for {typeName} '{name}'");
                return null;
            }
            catch (Exception ex)
            {
                Log($"CreateProjectItemReflection: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Try to set a property value safely (TrySetProperty protocol)
        /// </summary>
        private void TrySetPropertyValue(object item, string propertyName, object value)
        {
            try
            {
                // Type inference (reference: addProjectItemProperty)
                object typedValue = InferPropertyValue(value);

                // Step 1: Try direct Set
                try
                {
                    var setMethod = item.GetType().GetMethod("Set",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object) }, null);
                    if (setMethod != null)
                    {
                        setMethod.Invoke(item, new[] { propertyName, typedValue });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log($"TrySetPropertyValue: Set failed for '{propertyName}' — {ex.Message}, trying AddProperty");
                }

                // Step 2: Try AddProperty + Set
                try
                {
                    var hasPropMethod = item.GetType().GetMethod("HasProperty",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    var addPropMethod = item.GetType().GetMethod("AddProperty",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string) }, null);

                    bool hasProp = false;
                    if (hasPropMethod != null)
                    {
                        hasProp = (bool?)hasPropMethod.Invoke(item, new object[] { propertyName }) ?? false;
                    }

                    if (!hasProp && addPropMethod != null)
                    {
                        try
                        {
                            addPropMethod.Invoke(item, new object[] { propertyName });
                        }
                        catch { }
                    }

                    var setMethod = item.GetType().GetMethod("Set",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object) }, null);
                    if (setMethod != null)
                    {
                        setMethod.Invoke(item, new[] { propertyName, typedValue });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log($"TrySetPropertyValue: AddProperty+Set failed for '{propertyName}' — {ex.Message}");
                }

                Log($"TrySetPropertyValue: could not set '{propertyName}', skipping");
            }
            catch (Exception ex)
            {
                Log($"TrySetPropertyValue: unexpected error for '{propertyName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Infer property value type (reference: addProjectItemProperty type inference)
        /// </summary>
        private object InferPropertyValue(object value)
        {
            if (value == null) return "";

            if (value is JsonElement je)
            {
                return je.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => je.TryGetInt32(out var iv) ? (object)iv : je.GetDouble(),
                    JsonValueKind.String => je.GetString() ?? "",
                    _ => je.ToString() ?? ""
                };
            }

            var str = value.ToString() ?? "";

            // bool detection
            if (string.Equals(str, "true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(str, "false", StringComparison.OrdinalIgnoreCase))
                return false;

            // int detection
            if (decimal.TryParse(str, out var dVal) && dVal == Math.Floor(dVal) &&
                dVal >= int.MinValue && dVal <= int.MaxValue)
                return (int)dVal;

            // KzResourceID detection
            if (str.Contains("KzResourceID:"))
                return str;

            // Default: string
            return str;
        }

        /// <summary>
        /// Try to set a property using reflection (used for simple properties like StateGroupControllerPropertyTypeReference)
        /// </summary>
        private void TrySetProperty(object item, string propertyName, object value)
        {
            TrySetPropertyValue(item, propertyName, value);
        }

        #endregion

        #region State Manager Helpers

        /// <summary>
        /// Validate groupProperty exists in PropertyTypeLibrary and is a CustomEnumProperty.
        /// Also validates all statePropertyValues fall within enum options.
        /// </summary>
        private Dictionary<string, int>? ValidateGroupProperty(string groupProperty,
            List<StateDefinition> states, out string? error)
        {
            error = null;

            try
            {
                var project = GetActiveProject();
                if (project == null)
                {
                    error = "No active project";
                    return null;
                }

                var propertyLibObj = SafeGetProperty(project, "PropertyTypeLibrary");
                if (propertyLibObj == null)
                {
                    error = "Cannot access PropertyTypeLibrary";
                    return null;
                }

                var propTypesProp = propertyLibObj.GetType().GetProperty("ProjectPropertyTypes",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (propTypesProp == null)
                {
                    error = "Cannot access ProjectPropertyTypes";
                    return null;
                }

                var propTypes = propTypesProp.GetValue(propertyLibObj) as IEnumerable;
                if (propTypes == null)
                {
                    error = "ProjectPropertyTypes is empty";
                    return null;
                }

                object? foundProperty = null;
                foreach (var pt in propTypes)
                {
                    var ptName = SafeGetProperty(pt, "Name") as string;
                    if (ptName == groupProperty)
                    {
                        foundProperty = pt;
                        break;
                    }
                }

                if (foundProperty == null)
                {
                    error = $"Property '{groupProperty}' not found in PropertyTypeLibrary";
                    return null;
                }

                // Check if it's a CustomEnumProperty
                var ptTypeName = foundProperty.GetType().Name;
                if (!ptTypeName.Contains("CustomEnumProperty"))
                {
                    error = $"Property '{groupProperty}' is not a CustomEnumProperty (type: {ptTypeName})";
                    return null;
                }

                // Extract enum options
                var options = new Dictionary<string, int>();
                var optionsProp = SafeGetProperty(foundProperty, "Options");
                if (optionsProp is IDictionary dict)
                {
                    foreach (DictionaryEntry entry in dict)
                    {
                        var key = entry.Key?.ToString() ?? "";
                        var val = Convert.ToInt32(entry.Value);
                        options[key] = val;
                    }
                }
                else if (optionsProp is IEnumerable enumItems)
                {
                    foreach (var item in enumItems)
                    {
                        var key = SafeGetProperty(item, "Key")?.ToString() ?? "";
                        var val = SafeGetProperty(item, "Value");
                        if (val != null && !string.IsNullOrEmpty(key))
                            options[key] = Convert.ToInt32(val);
                    }
                }

                if (options.Count == 0)
                {
                    error = $"CustomEnumProperty '{groupProperty}' has no options";
                    return null;
                }

                // Validate each state's statePropertyValue falls within enum options
                var validValues = new HashSet<int>(options.Values);
                foreach (var state in states)
                {
                    if (!validValues.Contains(state.StatePropertyValue))
                    {
                        error = $"State '{state.StateName}' has statePropertyValue={state.StatePropertyValue} " +
                                $"which is not a valid value in enum '{groupProperty}'. " +
                                $"Valid values: [{string.Join(", ", options.Values)}]";
                        return null;
                    }
                }

                Log($"ValidateGroupProperty: '{groupProperty}' valid, {options.Count} enum options, {states.Count} states all valid");
                return options;
            }
            catch (Exception ex)
            {
                error = $"Failed to validate groupProperty: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Expand a template state into N auto-generated states.
        /// {0} in strings is replaced with the 1-based index.
        /// The statePropertyValue is set to the 1-based index.
        /// </summary>
        private static List<StateDefinition> GenerateStatesFromTemplate(StateDefinition template, int count)
        {
            var states = new List<StateDefinition>(count);
            for (int i = 1; i <= count; i++)
            {
                var sd = new StateDefinition
                {
                    StateName = ReplaceTemplate(template.StateName, i),
                    StatePropertyValue = i
                };

                if (template.Objects != null && template.Objects.Count > 0)
                {
                    sd.Objects = new List<StateObjectDefinition>();
                    foreach (var tplObj in template.Objects)
                    {
                        var od = new StateObjectDefinition
                        {
                            NodeName = ReplaceTemplate(tplObj.NodeName, i),
                            NodePath = ReplaceTemplate(tplObj.NodePath, i)
                        };

                        if (tplObj.Properties != null && tplObj.Properties.Count > 0)
                        {
                            od.Properties = new Dictionary<string, object>();
                            foreach (var kvp in tplObj.Properties)
                            {
                                var key = ReplaceTemplate(kvp.Key, i);
                                var value = kvp.Value;
                                if (value is string s)
                                    value = ReplaceTemplate(s, i);
                                else if (value is JsonElement je && je.ValueKind == JsonValueKind.String)
                                    value = ReplaceTemplate(je.GetString() ?? "", i);
                                od.Properties[key] = value;
                            }
                        }

                        sd.Objects.Add(od);
                    }
                }

                states.Add(sd);
            }
            return states;
        }

        private static string ReplaceTemplate(string input, int index)
        {
            return input.Replace("{0}", index.ToString());
        }

        /// <summary>
        /// Parse state definitions from JSON args
        /// </summary>
        private List<StateDefinition>? ParseStateDefinitions(JsonElement args)
        {
            if (!args.TryGetProperty("states", out var statesEl) || statesEl.ValueKind != JsonValueKind.Array)
                return null;

            var states = new List<StateDefinition>();
            foreach (var s in statesEl.EnumerateArray())
            {
                var sd = new StateDefinition
                {
                    StateName = s.TryGetProperty("stateName", out var sn) ? sn.GetString() ?? "" : "",
                    StatePropertyValue = s.TryGetProperty("statePropertyValue", out var spv) && spv.TryGetInt32(out var iv) ? iv : 0
                };

                if (string.IsNullOrEmpty(sd.StateName))
                {
                    Log("ParseStateDefinitions: state with empty name, skipping");
                    continue;
                }

                // Parse objects
                if (s.TryGetProperty("objects", out var objsEl) && objsEl.ValueKind == JsonValueKind.Array)
                {
                    sd.Objects = new List<StateObjectDefinition>();
                    foreach (var obj in objsEl.EnumerateArray())
                    {
                        var od = new StateObjectDefinition
                        {
                            NodeName = obj.TryGetProperty("nodeName", out var nn) ? nn.GetString() ?? "" : "",
                            NodePath = obj.TryGetProperty("nodePath", out var np) ? np.GetString() ?? "" : ""
                        };

                        // Parse properties
                        if (obj.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
                        {
                            od.Properties = new Dictionary<string, object>();
                            foreach (var prop in propsEl.EnumerateObject())
                            {
                                od.Properties[prop.Name] = prop.Value.Clone();
                            }
                        }

                        if (!string.IsNullOrEmpty(od.NodeName))
                            sd.Objects.Add(od);
                    }
                }

                states.Add(sd);
            }

            return states.Count > 0 ? states : null;
        }

        private sealed class ApplyBatchResolution
        {
            public List<StateDefinition> batchStates = null!;
            public int startIdx;
            public int endIdx;
            public int grandTotal;
            public int totalBatches;
            public bool isLastBatch;
            public bool partialPayload;
            public string? error;
        }

        /// <summary>
        /// Resolve which states to apply for the current batch.
        /// Supports full-array slicing (batchIndex * batchSize) and per-batch partial payloads.
        /// </summary>
        private static ApplyBatchResolution ResolveApplyBatch(
            List<StateDefinition> allStates, int batchIndex, int batchSize, int totalStateCountArg)
        {
            var resolution = new ApplyBatchResolution();
            var payloadCount = allStates.Count;

            if (payloadCount == 0)
            {
                resolution.error = "No states in request payload for apply batch.";
                return resolution;
            }

            var partialPayload = totalStateCountArg > payloadCount
                || (batchIndex > 0 && batchIndex * batchSize >= payloadCount);

            resolution.partialPayload = partialPayload;

            if (partialPayload)
            {
                resolution.batchStates = allStates;
                resolution.startIdx = batchIndex * batchSize;
                resolution.endIdx = resolution.startIdx + payloadCount;

                if (totalStateCountArg > 0)
                {
                    if (resolution.startIdx >= totalStateCountArg)
                    {
                        resolution.error =
                            $"batchIndex {batchIndex} starts at state {resolution.startIdx} but totalStateCount is {totalStateCountArg}.";
                        return resolution;
                    }

                    resolution.grandTotal = totalStateCountArg;
                    resolution.totalBatches = (int)Math.Ceiling((double)totalStateCountArg / batchSize);
                    resolution.isLastBatch = batchIndex >= resolution.totalBatches - 1;
                }
                else
                {
                    resolution.grandTotal = resolution.endIdx;
                    resolution.totalBatches = batchIndex + 1;
                    resolution.isLastBatch = payloadCount < batchSize;
                }

                return resolution;
            }

            resolution.grandTotal = payloadCount;
            resolution.totalBatches = (int)Math.Ceiling((double)payloadCount / batchSize);
            resolution.startIdx = batchIndex * batchSize;

            if (resolution.startIdx >= payloadCount)
            {
                resolution.error =
                    $"batchIndex {batchIndex} is out of range for {payloadCount} states. " +
                    "Send the full states array on every apply call, or send only this batch's states with totalStateCount set.";
                return resolution;
            }

            resolution.endIdx = Math.Min(resolution.startIdx + batchSize, payloadCount);
            var count = resolution.endIdx - resolution.startIdx;
            if (count <= 0)
            {
                resolution.error =
                    $"Invalid batch slice for batchIndex={batchIndex}, batchSize={batchSize}, payloadCount={payloadCount}.";
                return resolution;
            }

            resolution.batchStates = allStates.GetRange(resolution.startIdx, count);
            resolution.isLastBatch = batchIndex >= resolution.totalBatches - 1;
            return resolution;
        }

        /// <summary>
        /// Estimate time in ms for the operation
        /// </summary>
        private long EstimateTimeMs(int stateCount, int batchSize)
        {
            // Rough estimate: ~500ms per state for simple, ~2s for complex
            long perStateMs = 500;
            long baseMs = stateCount * perStateMs;
            // Add overhead for batches
            int batches = (int)Math.Ceiling((double)stateCount / batchSize);
            long overheadMs = batches * 2000; // 2s per batch overhead
            return baseMs + overheadMs;
        }

        /// <summary>
        /// WPF equivalent of Application.DoEvents() - pumps pending UI messages
        /// to prevent UI thread blocking and COM timeouts during batch operations.
        /// </summary>
        private static void PumpWpfMessages()
        {
            try
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new DispatcherOperationCallback(f =>
                    {
                        ((DispatcherFrame)f!).Continue = false;
                        return null;
                    }), frame);
                Dispatcher.PushFrame(frame);
            }
            catch
            {
                // Best-effort message pump; ignore failures
            }
        }

        /// <summary>
        /// Write progress to log
        /// </summary>
        private void WriteProgress(string msg)
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

        // Helper methods for JSON parsing
        private static string? GetStringArg(JsonElement args, string key)
        {
            return args.TryGetProperty(key, out var el) ? el.GetString() : null;
        }

        private static bool GetBoolArg(JsonElement args, string key)
        {
            return args.TryGetProperty(key, out var el) && el.GetBoolean();
        }

        private static int GetIntArg(JsonElement args, string key, int defaultValue)
        {
            return args.TryGetProperty(key, out var el) && el.TryGetInt32(out var iv) ? iv : defaultValue;
        }

        #endregion
    }

    #region State Manager DTOs

    /// <summary>
    /// State definition DTO
    /// </summary>
    internal class StateDefinition
    {
        public string StateName { get; set; } = "";
        public int StatePropertyValue { get; set; }
        public List<StateObjectDefinition>? Objects { get; set; }
    }

    /// <summary>
    /// StateObject definition DTO
    /// </summary>
    internal class StateObjectDefinition
    {
        public string NodeName { get; set; } = "";
        public string NodePath { get; set; } = "";
        public Dictionary<string, object>? Properties { get; set; }
    }

    #endregion
}
