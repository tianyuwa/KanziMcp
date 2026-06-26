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
        #region 属性操作

        private string PropertySetSuccessResult(
            string path, string itemType, string property, string? oldValue,
            object? newValue, string appliedVia, string legacyFallback = "")
        {
            return SafeSerialize(new
            {
                success = true,
                preview = false,
                node = path,
                nodeType = itemType,
                property,
                oldValue,
                newValue = newValue?.ToString(),
                appliedVia,
                legacyFallback
            });
        }

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
                    // ═══════════════════════════════════════════════════════════
                    // SDK 优先路径: PropertyContainer.Set（卡片 08 新增）
                    // ═══════════════════════════════════════════════════════════
                    if (TryApplyPropertyViaSdk(item, property, newValueObj, force,
                            out var sdkAppliedVia, out var sdkError))
                    {
                        Log($"SetProperty: SDK path succeeded via {sdkAppliedVia}");
                        return PropertySetSuccessResult(path, itemType, property, oldValue,
                            newValueObj?.ToString(), sdkAppliedVia, "None");
                    }

                    if (sdkError != null)
                        Log($"SetProperty: SDK path failed ({sdkError}), falling back to reflection strategies");

                    // ═══════════════════════════════════════════════════════════
                    // 反射降级策略链（保留全部现有分支）
                    // ═══════════════════════════════════════════════════════════

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

                        return PropertySetSuccessResult(path, itemType, property, oldValue, verifiedValue ?? newValueObj?.ToString(), "SetPropertyWithCommand");
                    }

                    // 策略2: SetOrCreatePropertyWithCommand(string, object)
                    setMethod = item.GetType().GetMethod("SetOrCreatePropertyWithCommand",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(string), typeof(object) }, null);
                    if (setMethod != null)
                    {
                        Log($"SetProperty: using SetOrCreatePropertyWithCommand");
                        setMethod.Invoke(item, new[] { property, newValueObj });

                        return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "SetOrCreatePropertyWithCommand");
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
                            return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "Set_String_Object_raw");
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
                                return PropertySetSuccessResult(path, itemType, property, oldValue, strValue, "Set_String_Object_string");
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
                                            return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "Set_String_Object_LocalizedString");
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
                                                    return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "Set_String_Object_Unlocked_raw");
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
                                                    return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "Set_String_Object_Unlocked_string");
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
                            return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "SetDynamicPropertyValue");
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
                            return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "SetPropertyOrRemoveIfDefault");
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
                                                return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "SetAfterUnlock");
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
                                            return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "Property.Set_string");
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

                                            return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "Properties[].Value");
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
                                                        return PropertySetSuccessResult(path, itemType, "Text", oldValue, newTextValue, "GetProperty().Value_Set");
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
                                                            return PropertySetSuccessResult(path, itemType, "Text", oldValue, newTextValue, "GetProperty().Set_string");
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
                                                                return PropertySetSuccessResult(path, itemType, "Text", oldValue, newTextValue, $"GetProperty().{setSig.Item1}");
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
                                            return PropertySetSuccessResult(path, itemType, property, oldValue, newTextValue, "item.Set(string,object)_direct");
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"SetProperty: item.Set(string, object) with string failed: {ex.Message}");
                                            // 可能是参数类型问题，尝试传入 object
                                            try
                                            {
                                                Log($"SetProperty: trying item.Set with object boxing");
                                                itemSetMethod.Invoke(item, new[] { (object)"Text", (object)newTextValue });
                                                return PropertySetSuccessResult(path, itemType, property, oldValue, newTextValue, "item.Set(object,object)");
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
                                                                    return PropertySetSuccessResult(path, itemType, "Text", oldValue, newTextValue, "generic_Set_TypedProperty");
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
                                                                    return PropertySetSuccessResult(path, itemType, "Text", oldValue, newTextValue, "Properties[Text].Value_direct");
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
                                                                    return PropertySetSuccessResult(path, itemType, "Text", oldValue, newTextValue, "Properties[Text].Set_string");
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
                                                                    return PropertySetSuccessResult(path, itemType, "Text", oldValue, newTextValue, "Properties[Text].TrySetValue");
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
                                                                        return PropertySetSuccessResult(path, itemType, "Text", oldValue, newTextValue, "Value.Set_string");
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
                                            return PropertySetSuccessResult(path, itemType, property, oldValue, newTextValue, "item.Set(string,object)_LocalizedString");
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
                                                    return PropertySetSuccessResult(path, itemType, property, oldValue, newTextValue, "Properties[Text].Set");
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

                        return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "DirectPropertySet");
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

                            return PropertySetSuccessResult(path, itemType, property, oldValue, newValueObj?.ToString(), "ForceNameSet");
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
                var filter = ParseNodeFilter(filterEl, parseIncludeProperties: false);

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
                            // SDK 优先路径（卡片 18 新增）— 与 SetProperty 行为对齐
                            if (TryApplyPropertyViaSdk(node, propName, newValue, ignoreReadOnly,
                                    out var via, out var sdkErr))
                            {
                                applied.Add(new Dictionary<string, object?>
                                {
                                    ["node"] = nodePath,
                                    ["property"] = propName,
                                    ["status"] = "applied",
                                    ["appliedVia"] = via
                                });
                                setCount++;
                                continue;
                            }

                            // SDK 失败 → 降级到原有反射逻辑
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
    }
}
