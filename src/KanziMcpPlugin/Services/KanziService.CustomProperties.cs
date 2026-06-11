// KanziService.CustomProperties.cs — Custom Enum Property 操作
//
// 文件作用: 创建/更新 CustomEnumProperty
// 关键方法: UpsertCustomEnumProperty
// 参考: createORupdateEnumProperty\UserControl1.xaml.cs (updateProperty, AddNameAndValue)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Rightware.Kanzi.Studio.PluginInterface;

namespace KanziMcpPlugin.Services
{
    public partial class KanziService
    {
        #region Custom Enum Property 操作

        /// <summary>
        /// 创建或更新 Custom Enum Property
        /// </summary>
        public string UpsertCustomEnumProperty(JsonElement? args)
        {
            if (!HasStudio)
                return ErrorJson("Kanzi Studio not connected");

            if (!args.HasValue)
                return ErrorJson("Missing arguments");

            var name = args.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var displayName = args.Value.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
            var category = args.Value.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "";
            var mode = args.Value.TryGetProperty("mode", out var m) ? m.GetString() ?? "preview" : "preview";

            if (string.IsNullOrEmpty(name))
                return ErrorJson("Missing 'name' parameter");

            // Parse options
            var options = new Dictionary<string, int>();
            if (args.Value.TryGetProperty("options", out var optsEl) && optsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in optsEl.EnumerateArray())
                {
                    var optName = opt.TryGetProperty("name", out var on) ? on.GetString() ?? "" : "";
                    var optValue = opt.TryGetProperty("value", out var ov) ? ov : default;

                    if (string.IsNullOrEmpty(optName))
                        return ErrorJson($"Option has empty name");

                    int intValue;
                    if (optValue.ValueKind == JsonValueKind.Number && optValue.TryGetInt32(out var iv))
                        intValue = iv;
                    else
                        return ErrorJson($"Option '{optName}' value must be an integer");

                    // Validate uniqueness of name (per spec: AddNameAndValue)
                    if (options.ContainsKey(optName))
                        return ErrorJson($"Duplicate option name: '{optName}'");

                    // Validate uniqueness of value
                    if (options.ContainsValue(intValue))
                        return ErrorJson($"Duplicate option value: {intValue}");

                    options[optName] = intValue;
                }
            }

            if (options.Count == 0)
                return ErrorJson("At least one option is required");

            // Defaults
            if (string.IsNullOrEmpty(displayName))
                displayName = "<Name>-" + name;

            try
            {
                var project = GetActiveProject();
                if (project == null)
                    return ErrorJson("No active project");

                // Get PropertyTypeLibrary
                var propertyLibObj = SafeGetProperty(project, "PropertyTypeLibrary");
                if (propertyLibObj == null)
                    return ErrorJson("Cannot access PropertyTypeLibrary");

                // Look for existing property by name
                object? existingProperty = null;
                bool isCustomEnum = false;

                var propTypesProp = propertyLibObj.GetType().GetProperty("ProjectPropertyTypes",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (propTypesProp != null)
                {
                    var propTypes = propTypesProp.GetValue(propertyLibObj) as IEnumerable;
                    if (propTypes != null)
                    {
                        foreach (var pt in propTypes)
                        {
                            var ptName = SafeGetProperty(pt, "Name") as string;
                            if (ptName == name)
                            {
                                existingProperty = pt;
                                // Check if it's a CustomEnumProperty
                                var ptTypeName = pt.GetType().Name;
                                isCustomEnum = ptTypeName.Contains("CustomEnumProperty");
                                break;
                            }
                        }
                    }
                }

                bool isCreate = (existingProperty == null);
                string action = isCreate ? "create" : "update";

                Log($"UpsertCustomEnumProperty: name={name}, action={action}, isCustomEnum={isCustomEnum}, mode={mode}");

                if (mode == "preview")
                {
                    return SafeSerialize(new
                    {
                        preview = true,
                        action,
                        name,
                        displayName,
                        category,
                        options = options.Select(o => new { name = o.Key, value = o.Value }).ToList()
                    });
                }

                // === Apply mode ===

                // Case B: exists but not CustomEnumProperty → delete and recreate
                if (!isCreate && !isCustomEnum)
                {
                    Log($"UpsertCustomEnumProperty: existing property is not CustomEnumProperty, deleting...");
                    try
                    {
                        var deleteMethod = propertyLibObj.GetType().GetMethod("DeleteProperty",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (deleteMethod != null)
                        {
                            deleteMethod.Invoke(propertyLibObj, new[] { existingProperty! });
                            Log($"UpsertCustomEnumProperty: deleted non-enum property '{name}'");
                            isCreate = true;
                            action = "create";
                        }
                        else
                        {
                            return ErrorJson($"Property '{name}' exists but is not a CustomEnumProperty and cannot be deleted automatically");
                        }
                    }
                    catch (Exception ex)
                    {
                        return ErrorJson($"Failed to delete non-enum property '{name}': {ex.Message}");
                    }
                }

                if (isCreate)
                {
                    // Case C: Create new CustomEnumProperty
                    var createMethod = propertyLibObj.GetType().GetMethod("CreateCustomEnumProperty",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (createMethod == null)
                    {
                        // Fallback: try reflection to find the method
                        foreach (var methodInfo in propertyLibObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                        {
                            if (methodInfo.Name == "CreateCustomEnumProperty" && methodInfo.GetParameters().Length == 4)
                            {
                                createMethod = methodInfo;
                                break;
                            }
                        }
                    }

                    if (createMethod == null)
                        return ErrorJson("CreateCustomEnumProperty method not found on PropertyTypeLibrary");

                    try
                    {
                        var newProp = createMethod.Invoke(propertyLibObj, new object[] { name, displayName, category, options });
                        Log($"UpsertCustomEnumProperty: created '{name}' successfully");

                        return SafeSerialize(new
                        {
                            success = true,
                            action = "create",
                            name,
                            displayName,
                            category,
                            options = options.Select(o => new { name = o.Key, value = o.Value }).ToList()
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"UpsertCustomEnumProperty: CreateCustomEnumProperty failed: {ex.Message}");
                        if (ex.InnerException != null)
                            Log($"  InnerException: {ex.InnerException.Message}");
                        return ErrorJson($"Failed to create CustomEnumProperty '{name}': {ex.Message}");
                    }
                }
                else
                {
                    // Case A: exists and is CustomEnumProperty → update
                    try
                    {
                        // Set Options
                        var optionsProp = existingProperty!.GetType().GetProperty("Options",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (optionsProp != null && optionsProp.CanWrite)
                        {
                            optionsProp.SetValue(existingProperty, options);
                        }
                        else
                        {
                            // Try Set method
                            var setMethod = existingProperty.GetType().GetMethod("Set",
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                                null, new[] { typeof(string), typeof(object) }, null);
                            if (setMethod != null)
                                setMethod.Invoke(existingProperty, new object[] { "Options", options });
                            else
                                return ErrorJson("Cannot set Options on CustomEnumProperty");
                        }

                        // Set DisplayName
                        var displayNameProp = existingProperty.GetType().GetProperty("DisplayName",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (displayNameProp != null && displayNameProp.CanWrite)
                            displayNameProp.SetValue(existingProperty, displayName);

                        // Set Category
                        var categoryProp = existingProperty.GetType().GetProperty("Category",
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (categoryProp != null && categoryProp.CanWrite)
                            categoryProp.SetValue(existingProperty, category);

                        Log($"UpsertCustomEnumProperty: updated '{name}' successfully");

                        return SafeSerialize(new
                        {
                            success = true,
                            action = "update",
                            name,
                            displayName,
                            category,
                            options = options.Select(o => new { name = o.Key, value = o.Value }).ToList()
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"UpsertCustomEnumProperty: update failed: {ex.Message}");
                        return ErrorJson($"Failed to update CustomEnumProperty '{name}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"UpsertCustomEnumProperty failed: {ex.Message}");
                return ErrorJson($"UpsertCustomEnumProperty failed: {ex.Message}");
            }
        }

        #endregion
    }
}
