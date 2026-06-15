// ToolHandler.cs
//
// 文件作用: MCP 工具定义与执行路由（KanziMcpServer 核心）
// 关键类: ToolHandler
// 主要职责:
//   1. 定义所有 MCP 工具（kanzi_query_nodes / kanzi_set_node_property / kanzi_search_nodes 等）
//   2. 将 MCP 工具的 arguments 转换为对 KanziPipeClient 的调用
//   3. 构造符合 MCP 协议的响应格式（content 数组 + isError）
//   4. 参数校验和默认值填充（如 searchIn 默认 ["Name","Path"]）
// 工具列表（共 11 个）:
//   - kanzi_status                   : 查询 Kanzi Studio 连接状态
//   - kanzi_query_nodes              : 按类型/名称/路径查询节点
//   - kanzi_get_node_tree            : 获取节点树（层级结构）
//   - kanzi_list_node_types          : 列出所有节点类型
//   - kanzi_get_binding_info        : 获取节点数据绑定信息
//   - kanzi_set_node_property       : 设置节点属性（set/apply 双模式）
//   - kanzi_batch_set_property      : 批量设置属性
//   - kanzi_get_property_metadata   : 获取属性元数据
//   - kanzi_audit_bindings         : 审计/修改数据绑定
//   - kanzi_audit_project_structure : 审计项目结构
//   - kanzi_search_nodes            : 全文搜索节点（支持 Name/Path/Type/Text）
// 依赖: KanziPipeClient（通过 Named Pipe 与 Kanzi Plugin 通信）

using System.Text.Json;
using KanziMcpServer.Models;
using KanziMcpServer.Services;

namespace KanziMcpServer.Handlers;

/// <summary>
/// 工具处理器 - 定义和执行 MCP 工具
/// </summary>
public class ToolHandler
{
    private readonly KanziPipeClient _pipeClient;

    public ToolHandler(KanziPipeClient pipeClient)
    {
        _pipeClient = pipeClient;
    }

    /// <summary>
    /// 获取所有工具定义
    /// </summary>
    public List<ToolDefinition> GetToolDefinitions()
    {
        return new List<ToolDefinition>
        {
            // ========== 查询工具 ==========

            GetQueryNodesTool(),
            GetGetNodeTreeTool(),
            GetListNodeTypesTool(),
            GetGetBindingInfoTool(),

            // ========== 属性操作工具 ==========

            GetSetNodePropertyTool(),
            GetBatchSetPropertyTool(),
            GetGetPropertyMetadataTool(),

            // ========== 审计工具 ==========

            GetAuditBindingsTool(),
            GetAuditProjectStructureTool(),

            // ========== 节点创建与删除 ==========

            GetCreateNodeTool(),
            GetDeleteNodeTool(),

            // ========== 资源导入 ==========

            GetImportImageTool(),
            GetImportFbxTool(),

            // ========== 资源诊断 ==========

            GetDoctorResourceTool(),

            // ========== 自定义属性工具 ==========

            GetUpsertCustomEnumPropertyTool(),
            GetCreateStateManagerTool(),

            // ========== 实用工具 ==========

            GetGetServerStatusTool(),
            GetSearchNodesTool()
        };
    }

    /// <summary>
    /// 执行工具
    /// </summary>
    public async Task<string> ExecuteToolAsync(string toolName, JsonElement args)
    {
        try
        {
            return toolName switch
            {
                // 查询工具
                "kanzi_query_nodes" => await ExecuteQueryNodesAsync(args),
                "kanzi_get_node_tree" => await ExecuteGetNodeTreeAsync(args),
                "kanzi_list_node_types" => await ExecuteListNodeTypesAsync(args),
                "kanzi_get_binding_info" => await ExecuteGetBindingInfoAsync(args),

                // 属性操作工具
                "kanzi_set_node_property" => await ExecuteSetPropertyAsync(args),
                "kanzi_batch_set_property" => await ExecuteBatchSetPropertyAsync(args),
                "kanzi_get_property_metadata" => await ExecuteGetPropertyMetadataAsync(args),

                // 审计工具
                "kanzi_audit_bindings" => await ExecuteAuditBindingsAsync(args),
                "kanzi_audit_localization" => ExecuteAuditLocalizationDeprecated(),
                "kanzi_audit_project_structure" => await ExecuteAuditProjectStructureAsync(args),
                "kanzi_audit_resource_references" => await ExecuteAuditResourceReferencesCompatAsync(args),

                // 节点创建与删除
                "kanzi_create_node" => await ExecuteCreateNodeAsync(args),
                "kanzi_delete_node" => await ExecuteDeleteNodeAsync(args),

                // 资源导入
                "kanzi_import_image" => await ExecuteImportImageAsync(args),
                "kanzi_import_fbx" => await ExecuteImportFbxAsync(args),

                // 资源诊断
                "kanzi_doctor_resource" => await ExecuteDoctorResourceAsync(args),

                // 自定义属性
                "kanzi_upsert_custom_enum_property" => await ExecuteUpsertCustomEnumPropertyAsync(args),
                "kanzi_create_state_manager" => await ExecuteCreateStateManagerAsync(args),

                // 实用工具
                "kanzi_get_status" => await ExecuteGetStatusAsync(),
                "kanzi_search_nodes" => await ExecuteSearchNodesAsync(args),

                _ => JsonSerializer.Serialize(new { error = $"未知工具: {toolName}" })
            };
        }
        catch (Exception)  // 不再吞掉异常，向上抛出，由 McpProtocolHandler 统一处理为标准 JSON-RPC 错误响应
        {
            throw;
        }
    }

    #region 工具定义

    private static ToolDefinition GetQueryNodesTool() => new()
    {
        Name = "kanzi_query_nodes",
        Description = "Query Kanzi nodes by type, name, or path. Returns detailed node information including properties if requested.",
        InputSchema = Schema(new[]
        {
            Prop("type", "string", "Node type filter (e.g., 'TextBlock2D', 'Image2D', 'Button2D')"),
            Prop("name", "string", "Node name filter, supports wildcards (*). Example: '*标题*' matches nodes containing '标题'"),
            Prop("path", "string", "Node path prefix. Example: '/MainScreen/Header'"),
            Prop("includeProperties", "boolean", "Include node properties in response", defaultValue: false),
            Prop("includeBindings", "boolean", "Include data binding information", defaultValue: false),
            Prop("recursive", "boolean", "Search recursively", defaultValue: true),
            Prop("limit", "integer", "Maximum number of results", defaultValue: 1000),
        })
    };

    private static ToolDefinition GetGetNodeTreeTool() => new()
    {
        Name = "kanzi_get_node_tree",
        Description = "Get the hierarchical node tree structure starting from a specified root node.",
        InputSchema = Schema(new[]
        {
            Prop("rootPath", "string", "Root node path. Leave empty for project root."),
            Prop("depth", "integer", "Maximum depth to traverse", defaultValue: 3),
            Prop("includeProperties", "boolean", "Include properties", defaultValue: false),
        })
    };

    private static ToolDefinition GetListNodeTypesTool() => new()
    {
        Name = "kanzi_list_node_types",
        Description = "List all available Kanzi node types with their descriptions and property counts.",
        InputSchema = Schema(Array.Empty<Dictionary<string, object>>())
    };

    private static ToolDefinition GetGetBindingInfoTool() => new()
    {
        Name = "kanzi_get_binding_info",
        Description = "Get detailed data binding information for a specific node.",
        InputSchema = Schema(new[]
        {
            Prop("path", "string", "Full node path"),
            Prop("includeMetadata", "boolean", "Include binding metadata", defaultValue: false),
        }, required: new[] { "path" })
    };

    private static ToolDefinition GetSetNodePropertyTool() => new()
    {
        Name = "kanzi_set_node_property",
        Description = "Set a single property on a node. Use preview mode to check changes before applying.",
        InputSchema = Schema(new[]
        {
            Prop("path", "string", "Full node path (e.g., '/MainScreen/Header/TitleText')"),
            Prop("property", "string", "Property name (e.g., 'FontColor', 'TextConcept.Text', 'Opacity')"),
            Prop("value", "object", "Property value (scalar, color {r,g,b,a}, or vector {x,y})"),
            Prop("mode", "string", "'preview' checks without applying, 'apply' makes the change", defaultValue: "preview", enumValues: new[] { "preview", "apply" }),
            Prop("force", "boolean", "Force set even if read-only", defaultValue: false),
        }, required: new[] { "path", "property", "value" })
    };

    private static ToolDefinition GetBatchSetPropertyTool() => new()
    {
        Name = "kanzi_batch_set_property",
        Description = "Batch set properties on multiple nodes matching a filter. Always preview first!",
        InputSchema = Schema(new[]
        {
            Prop("filter", "object", "Node filter criteria"),
            Prop("properties", "object", "Properties to set as key-value pairs"),
            Prop("mode", "string", "'preview' or 'apply'", defaultValue: "preview", enumValues: new[] { "preview", "apply" }),
            Prop("ignoreReadOnly", "boolean", "Skip read-only properties", defaultValue: false),
        }, required: new[] { "filter", "properties" })
    };

    private static ToolDefinition GetGetPropertyMetadataTool() => new()
    {
        Name = "kanzi_get_property_metadata",
        Description = "Get property metadata for a node type (data type, read-only status, default value).",
        InputSchema = Schema(new[]
        {
            Prop("nodeType", "string", "Node type name (e.g., 'TextBlock2D')"),
        }, required: new[] { "nodeType" })
    };

    private static ToolDefinition GetAuditBindingsTool() => new()
    {
        Name = "kanzi_audit_bindings",
        Description = "Audit and optionally modify data bindings. Detects empty binding codes, duplicate codes across nodes, and unresolved target properties. Supports preview/apply binding code updates via modifications.",
        InputSchema = Schema(new[]
        {
            Prop("path", "string", "Root path to audit (default: entire project)"),
            Prop("checkPriority", "boolean", "Check for duplicate binding codes across nodes", defaultValue: true),
            Prop("findOrphans", "boolean", "Find bindings whose target property could not be resolved", defaultValue: true),
            Prop("modifications", "array", "Optional binding updates: [{ nodePath, bindingIndex|property, code, mode: preview|apply }]"),
        })
    };

    private static ToolDefinition GetAuditProjectStructureTool() => new()
    {
        Name = "kanzi_audit_project_structure",
        Description = "Audit project structure for naming conventions and organization best practices.",
        InputSchema = Schema(new[]
        {
            Prop("namingPattern", "string", "Regex pattern for naming convention"),
            Prop("checkDepth", "boolean", "Check for excessively deep nesting", defaultValue: true),
            Prop("checkNaming", "boolean", "Check naming conventions", defaultValue: true),
        })
    };

    private static ToolDefinition GetGetServerStatusTool() => new()
    {
        Name = "kanzi_get_status",
        Description = "Get MCP server and Kanzi connection status.",
        InputSchema = Schema(Array.Empty<Dictionary<string, object>>())
    };

    private static ToolDefinition GetSearchNodesTool() => new()
    {
        Name = "kanzi_search_nodes",
        Description = "Search nodes by name, path, type, or text content. Default searches Name and Path.",
        InputSchema = Schema(new[]
        {
            Prop("searchText", "string", "Text to search for"),
            Prop("searchIn", "array", "Properties to search in (default: ['Name', 'Path'], options: 'Name', 'Path', 'Type', 'Text')"),
            Prop("caseSensitive", "boolean", "Case sensitive search", defaultValue: false),
        }, required: new[] { "searchText" })
    };

    private static ToolDefinition GetCreateNodeTool() => new()
    {
        Name = "kanzi_create_node",
        Description = "Create a new node under a parent node. Use this to add nodes like EmptyNode2D, TextBlock2D, etc.",
        InputSchema = Schema(new[]
        {
            Prop("parentPath", "string", "Parent node path where the new node will be created"),
            Prop("nodeType", "string", "Node type (e.g., 'EmptyNode2D', 'TextBlock2D', 'RectangleNode2D', 'Image2D')"),
            Prop("nodeName", "string", "Name for the new node (optional)"),
            Prop("properties", "object", "Initial properties to set on the new node (optional)"),
        }, required: new[] { "parentPath", "nodeType" })
    };

    private static ToolDefinition GetDeleteNodeTool() => new()
    {
        Name = "kanzi_delete_node",
        Description = "Delete a node. Use preview/dry-run mode first to see what will be deleted. CAUTION: This deletes the node and all its children!",
        InputSchema = Schema(new[]
        {
            Prop("path", "string", "Full path of the node to delete"),
            Prop("mode", "string", "'preview' or 'dry-run' to see what will be deleted without actually deleting, 'apply' to delete", defaultValue: "apply", enumValues: new[] { "preview", "dry-run", "apply" }),
        }, required: new[] { "path" })
    };

    private static ToolDefinition GetImportImageTool() => new()
    {
        Name = "kanzi_import_image",
        Description = "Import one or many image files into the Kanzi resource library (Textures folder). Supported formats: PNG, JPG, BMP, etc.",
        InputSchema = Schema(new[]
        {
            Prop("filePath", "string", "Full path to a single image file (use filePaths for batch import)"),
            Prop("filePaths", "array", "Batch import: array of full paths to image files"),
            Prop("resourceName", "string", "Optional name for the imported resource (single filePath only)"),
            Prop("targetFolder", "string", "Target resource folder (default: 'Textures')", defaultValue: "Textures"),
        })
    };

    private static ToolDefinition GetImportFbxTool() => new()
    {
        Name = "kanzi_import_fbx",
        Description = "Import a 3D model (FBX format) into the Kanzi resource library (Meshes folder).",
        InputSchema = Schema(new[]
        {
            Prop("filePath", "string", "Full path to the FBX file on your computer"),
            Prop("resourceName", "string", "Optional name for the imported resource"),
            Prop("targetFolder", "string", "Target resource folder (default: 'Meshes')", defaultValue: "Meshes"),
        }, required: new[] { "filePath" })
    };

    private static ToolDefinition GetDoctorResourceTool() => new()
    {
        Name = "kanzi_doctor_resource",
        Description = "Diagnose project resources — find unused Image/Texture resources and optionally detect missing texture files on disk.",
        InputSchema = Schema(new[]
        {
            Prop("checkImages", "boolean", "Check for unused images", defaultValue: true),
            Prop("checkTextures", "boolean", "Check for unused textures", defaultValue: true),
            Prop("checkBroken", "boolean", "Check texture source files exist on disk", defaultValue: false),
            Prop("resourceFolders", "array", "Resource library folders to scan (default: [\"Textures\"])"),
        })
    };

    private static ToolDefinition GetUpsertCustomEnumPropertyTool() => new()
    {
        Name = "kanzi_upsert_custom_enum_property",
        Description = "Create or update a Custom Enum Property in the project. If a property with the same name already exists and is a CustomEnumProperty, it updates the options/displayName/category. If it exists but is a different type, it deletes and recreates. If it does not exist, it creates a new one.",
        InputSchema = Schema(new[]
        {
            Prop("name", "string", "Property name (e.g., 'WarningValue', 'PopState')"),
            Prop("options", "array", "Array of { name: string, value: int } objects defining the enum options"),
            Prop("displayName", "string", "Display name for the property (default: '<Name>-name')"),
            Prop("category", "string", "Category for the property (default: '')"),
            Prop("mode", "string", "'preview' checks without applying, 'apply' makes the change", defaultValue: "preview", enumValues: new[] { "preview", "apply" }),
        }, required: new[] { "name", "options" })
    };

    private static ToolDefinition GetCreateStateManagerTool() => new()
    {
        Name = "kanzi_create_state_manager",
        Description = @"Create a State Manager with StateGroup, States, and StateObjects. Supports batched creation for large state counts.

Usage order:
1. First call kanzi_upsert_custom_enum_property to ensure the groupProperty exists
2. Then call kanzi_create_state_manager with mode=preview to see the batch plan
3. Large jobs: autoGenerateCount + 1 template (use {0} in strings), batchSize=12..16, loop batchIndex with mode=apply
4. Or per-batch states with totalStateCount
5. If stateCount > 200, must set confirmLargeBatch=true
6. Not recommended to exceed 500 states per group; split into multiple StateGroups instead",
        InputSchema = Schema(new[]
        {
            Prop("managerName", "string", "Name of the State Manager"),
            Prop("groupName", "string", "Name of the State Group"),
            Prop("groupProperty", "string", "Property name for the group controller (must be a CustomEnumProperty)"),
            Prop("states", "array", "State definitions, or one template when autoGenerateCount is set ({0} = index)"),
            Prop("bindNodePath", "string", "Path of the node to bind the StateManager to (e.g., 'Screens/Screen/RootPage/Viewport')"),
            Prop("mode", "string", "'preview' or 'apply'", defaultValue: "preview", enumValues: new[] { "preview", "apply" }),
            Prop("confirmLargeBatch", "boolean", "Required true when stateCount > 200", defaultValue: false),
            Prop("batchIndex", "integer", "Batch index for incremental apply (0-based)", defaultValue: 0),
            Prop("batchSize", "integer", "States per batch (max 16 with autoGenerate/totalStateCount; default 12)", defaultValue: 12),
            Prop("totalStateCount", "integer", "Total states when sending per-batch subsets (optional)", defaultValue: 0),
            Prop("autoGenerateCount", "integer", "Generate N states from first template; use with batchIndex (optional)", defaultValue: 0),
            Prop("strategy", "string", "Creation strategy: 'auto', 'clone', or 'direct'", defaultValue: "auto", enumValues: new[] { "auto", "clone", "direct" }),
        }, required: new[] { "managerName", "groupName", "groupProperty", "states" })
    };

    #endregion

    #region JSON Schema Helpers

    /// <summary>
    /// 构建 JSON Schema object
    /// </summary>
    private static Dictionary<string, object> Schema(Dictionary<string, object>[] properties, string[]? required = null)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties.Length > 0
                ? properties.ToDictionary(p => (string)p["name"]!, p => p["schema"]!)
                : new Dictionary<string, object>()
        };
        if (required is { Length: > 0 })
        {
            schema["required"] = required;
        }
        return schema;
    }

    /// <summary>
    /// 构建属性定义
    /// </summary>
    private static Dictionary<string, object> Prop(string name, string type, string description, object? defaultValue = null, string[]? enumValues = null)
    {
        var propSchema = new Dictionary<string, object>
        {
            ["type"] = type,
            ["description"] = description
        };
        if (defaultValue != null)
        {
            propSchema["default"] = defaultValue;
        }
        if (enumValues != null)
        {
            propSchema["enum"] = enumValues;
        }
        return new Dictionary<string, object>
        {
            ["name"] = name,
            ["schema"] = propSchema
        };
    }

    #endregion

    #region 工具执行

    private async Task<string> ExecuteQueryNodesAsync(JsonElement args)
    {
        var filter = ParseQueryFilter(args);
        return await _pipeClient.QueryNodesAsync(filter);
    }

    private async Task<string> ExecuteGetNodeTreeAsync(JsonElement args)
    {
        string? rootPath = null;
        int depth = 3;
        bool includeProperties = false;

        if (args.TryGetProperty("rootPath", out var pathEl))
            rootPath = pathEl.GetString();
        if (args.TryGetProperty("depth", out var depthEl))
            depth = depthEl.GetInt32();
        if (args.TryGetProperty("includeProperties", out var propsEl))
            includeProperties = propsEl.GetBoolean();

        return await _pipeClient.GetNodeTreeAsync(rootPath, depth, includeProperties);
    }

    private async Task<string> ExecuteListNodeTypesAsync(JsonElement args)
    {
        return await _pipeClient.ListNodeTypesAsync();
    }

    private async Task<string> ExecuteGetBindingInfoAsync(JsonElement args)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : "";
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("缺少 path 参数");

        return await _pipeClient.GetBindingInfoAsync(path);
    }

    private async Task<string> ExecuteSetPropertyAsync(JsonElement args)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : "";
        var property = args.TryGetProperty("property", out var pr) ? pr.GetString() : "";
        var value = args.TryGetProperty("value", out var v) ? v : default;
        var mode = args.TryGetProperty("mode", out var m) ? m.GetString() ?? "preview" : "preview";
        var force = args.TryGetProperty("force", out var f) && f.GetBoolean();

        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(property))
            throw new ArgumentException("缺少 path 或 property 参数");

        return await _pipeClient.SetPropertyAsync(path, property, value, mode, force);
    }

    private async Task<string> ExecuteBatchSetPropertyAsync(JsonElement args)
    {
        var filter = ParseQueryFilter(args.TryGetProperty("filter", out var f) ? f : default);
        var properties = ParseProperties(args.TryGetProperty("properties", out var p) ? p : default);
        var mode = args.TryGetProperty("mode", out var m) ? m.GetString() ?? "preview" : "preview";
        var ignoreReadOnly = args.TryGetProperty("ignoreReadOnly", out var i) && i.GetBoolean();

        return await _pipeClient.BatchSetPropertyAsync(filter, properties, mode, ignoreReadOnly);
    }

    private async Task<string> ExecuteGetPropertyMetadataAsync(JsonElement args)
    {
        var nodeType = args.TryGetProperty("nodeType", out var nt) ? nt.GetString() : "";
        if (string.IsNullOrEmpty(nodeType))
            throw new ArgumentException("缺少 nodeType 参数");

        return await _pipeClient.GetPropertyMetadataAsync(nodeType);
    }

    private async Task<string> ExecuteAuditBindingsAsync(JsonElement args)
    {
        return await _pipeClient.AuditBindingsAsync(args);
    }

    private static string ExecuteAuditLocalizationDeprecated()
        => AuditCompatMapper.BuildLocalizationDeprecatedJson();

    private async Task<string> ExecuteAuditResourceReferencesCompatAsync(JsonElement args)
    {
        var checkUnused = !args.TryGetProperty("checkUnused", out var cu) || cu.GetBoolean();
        var checkBroken = !args.TryGetProperty("checkBroken", out var cb) || cb.GetBoolean();
        var checkOrphaned = !args.TryGetProperty("checkOrphaned", out var co) || co.GetBoolean();

        var doctorJson = await _pipeClient.DoctorResourceAsync(
            checkImages: checkUnused,
            checkTextures: checkUnused,
            checkBroken: checkBroken,
            resourceFolders: new List<string> { "Textures" });

        return AuditCompatMapper.MapDoctorJsonToResourceReferencesCompat(doctorJson, checkOrphaned);
    }

    private async Task<string> ExecuteAuditProjectStructureAsync(JsonElement args)
    {
        var namingPattern = args.TryGetProperty("namingPattern", out var np) ? np.GetString() : null;
        var checkDepth = args.TryGetProperty("checkDepth", out var cd) && cd.GetBoolean();
        var checkNaming = args.TryGetProperty("checkNaming", out var cn) && cn.GetBoolean();

        return await _pipeClient.AuditProjectStructureAsync(namingPattern, checkDepth, checkNaming);
    }

    private async Task<string> ExecuteDoctorResourceAsync(JsonElement args)
    {
        var checkImages = !args.TryGetProperty("checkImages", out var ci) || ci.GetBoolean();
        var checkTextures = !args.TryGetProperty("checkTextures", out var ct) || ct.GetBoolean();
        var checkBroken = args.TryGetProperty("checkBroken", out var cb) && cb.GetBoolean();

        var resourceFolders = new List<string> { "Textures" };
        if (args.TryGetProperty("resourceFolders", out var foldersEl) && foldersEl.ValueKind == JsonValueKind.Array)
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

        return await _pipeClient.DoctorResourceAsync(checkImages, checkTextures, checkBroken, resourceFolders);
    }

    private async Task<string> ExecuteCreateNodeAsync(JsonElement args)
    {
        var parentPath = args.TryGetProperty("parentPath", out var pp) ? pp.GetString() ?? "" : "";
        var nodeType = args.TryGetProperty("nodeType", out var nt) ? nt.GetString() ?? "" : "";
        var nodeName = args.TryGetProperty("nodeName", out var nn) ? nn.GetString() : null;
        Dictionary<string, object>? properties = null;
        if (args.TryGetProperty("properties", out var props) && props.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            properties = new Dictionary<string, object>();
            foreach (var prop in props.EnumerateObject())
            {
                properties[prop.Name] = prop.Value;
            }
        }

        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(nodeType))
            throw new ArgumentException("缺少 parentPath 或 nodeType 参数");

        return await _pipeClient.CreateNodeAsync(parentPath, nodeType, nodeName, properties);
    }

    private async Task<string> ExecuteDeleteNodeAsync(JsonElement args)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        var mode = args.TryGetProperty("mode", out var m) ? m.GetString() ?? "apply" : "apply";

        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Missing path parameter");

        return await _pipeClient.DeleteNodeAsync(path, mode);
    }

    private async Task<string> ExecuteImportImageAsync(JsonElement args)
    {
        var hasFilePath = args.TryGetProperty("filePath", out var fp) && fp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(fp.GetString());
        var hasFilePaths = args.TryGetProperty("filePaths", out var fps) && fps.ValueKind == JsonValueKind.Array
            && fps.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()));

        if (!hasFilePath && !hasFilePaths)
            throw new ArgumentException("Missing filePath or filePaths parameter");

        return await _pipeClient.ImportImageAsync(args);
    }

    private async Task<string> ExecuteImportFbxAsync(JsonElement args)
    {
        var filePath = args.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? "" : "";
        var resourceName = args.TryGetProperty("resourceName", out var rn) ? rn.GetString() : null;
        var targetFolder = args.TryGetProperty("targetFolder", out var tf) ? tf.GetString() ?? "Meshes" : "Meshes";

        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException("Missing filePath parameter");

        return await _pipeClient.ImportFbxAsync(filePath, resourceName, targetFolder);
    }

    private async Task<string> ExecuteUpsertCustomEnumPropertyAsync(JsonElement args)
    {
        var name = args.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var displayName = args.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
        var category = args.TryGetProperty("category", out var cat) ? cat.GetString() : null;
        var mode = args.TryGetProperty("mode", out var m) ? m.GetString() ?? "preview" : "preview";

        var options = new List<Dictionary<string, object>>();
        if (args.TryGetProperty("options", out var optsEl) && optsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in optsEl.EnumerateArray())
            {
                var optDict = new Dictionary<string, object>();
                if (opt.TryGetProperty("name", out var on))
                    optDict["name"] = on.GetString() ?? "";
                if (opt.TryGetProperty("value", out var ov))
                {
                    if (ov.ValueKind == JsonValueKind.Number && ov.TryGetInt32(out var iv))
                        optDict["value"] = iv;
                    else
                        optDict["value"] = ov.ToString() ?? "";
                }
                options.Add(optDict);
            }
        }

        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Missing 'name' parameter");

        return await _pipeClient.UpsertCustomEnumPropertyAsync(name, options, displayName, category, mode);
    }

    private async Task<string> ExecuteCreateStateManagerAsync(JsonElement args)
    {
        var managerName = args.TryGetProperty("managerName", out var mn) ? mn.GetString() ?? "" : "";
        var groupName = args.TryGetProperty("groupName", out var gn) ? gn.GetString() ?? "" : "";
        var groupProperty = args.TryGetProperty("groupProperty", out var gp) ? gp.GetString() ?? "" : "";
        var bindNodePath = args.TryGetProperty("bindNodePath", out var bnp) ? bnp.GetString() ?? "" : "";
        var mode = args.TryGetProperty("mode", out var m) ? m.GetString() ?? "preview" : "preview";
        var confirmLargeBatch = args.TryGetProperty("confirmLargeBatch", out var clb) && clb.GetBoolean();
        var batchIndex = args.TryGetProperty("batchIndex", out var bi) ? bi.GetInt32() : 0;
        var batchSize = args.TryGetProperty("batchSize", out var bs) ? Math.Min(bs.GetInt32(), 100) : McpConstants.StateManagerDefaultBatchSize;
        var totalStateCount = args.TryGetProperty("totalStateCount", out var tsc) ? tsc.GetInt32() : 0;
        var autoGenerateCount = args.TryGetProperty("autoGenerateCount", out var agc) ? agc.GetInt32() : 0;
        var strategy = args.TryGetProperty("strategy", out var st) ? st.GetString() ?? "auto" : "auto";

        var states = new List<Dictionary<string, object>>();
        if (args.TryGetProperty("states", out var statesEl) && statesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var state in statesEl.EnumerateArray())
            {
                var stateDict = new Dictionary<string, object>();
                if (state.TryGetProperty("stateName", out var sn))
                    stateDict["stateName"] = sn.GetString() ?? "";
                if (state.TryGetProperty("statePropertyValue", out var spv))
                {
                    if (spv.ValueKind == JsonValueKind.Number && spv.TryGetInt32(out var iv))
                        stateDict["statePropertyValue"] = iv;
                    else
                        stateDict["statePropertyValue"] = spv.ToString() ?? "";
                }

                var objects = new List<Dictionary<string, object>>();
                if (state.TryGetProperty("objects", out var objsEl) && objsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var obj in objsEl.EnumerateArray())
                    {
                        var objDict = new Dictionary<string, object>();
                        if (obj.TryGetProperty("nodeName", out var nn))
                            objDict["nodeName"] = nn.GetString() ?? "";
                        if (obj.TryGetProperty("nodePath", out var np))
                            objDict["nodePath"] = np.GetString() ?? "";

                        var props = new Dictionary<string, object>();
                        if (obj.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in propsEl.EnumerateObject())
                            {
                                var propValue = prop.Value;
                                props[prop.Name] = propValue.ValueKind switch
                                {
                                    JsonValueKind.String => propValue.GetString() ?? "",
                                    JsonValueKind.Number => propValue.TryGetInt32(out var iv) ? (object)iv : propValue.GetDouble(),
                                    JsonValueKind.True => true,
                                    JsonValueKind.False => false,
                                    JsonValueKind.Null => null!,
                                    _ => propValue.ToString() ?? ""
                                };
                            }
                        }
                        objDict["properties"] = props;
                        objects.Add(objDict);
                    }
                }
                stateDict["objects"] = objects;
                states.Add(stateDict);
            }
        }

        if (string.IsNullOrEmpty(managerName) || string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(groupProperty))
            throw new ArgumentException("Missing required parameters: managerName, groupName, groupProperty");

        var stateCount = states.Count;
        var grandTotal = totalStateCount > 0 ? totalStateCount
            : (autoGenerateCount > 0 ? autoGenerateCount : stateCount);

        if (batchSize > McpConstants.StateManagerMaxApplyBatchSize)
            batchSize = McpConstants.StateManagerMaxApplyBatchSize;

        var isPartialOrGenerated = totalStateCount > 0 || autoGenerateCount > 0;
        if (grandTotal >= 9 && batchSize > McpConstants.StateManagerRecommendedBatchSize
            && !isPartialOrGenerated && stateCount >= 9)
        {
            Console.Error.WriteLine(
                $"[ToolHandler] create_state_manager: clamping batchSize {batchSize} -> {McpConstants.StateManagerRecommendedBatchSize} (full-array apply, stateCount={stateCount})");
            batchSize = McpConstants.StateManagerRecommendedBatchSize;
        }

        var partialPayload = totalStateCount > stateCount
            || autoGenerateCount > 0
            || (batchIndex > 0 && batchIndex * batchSize >= stateCount);
        var statesInBatch = isPartialOrGenerated
            ? Math.Min(batchSize, Math.Max(0, grandTotal - batchIndex * batchSize))
            : partialPayload
                ? stateCount
                : Math.Min(batchSize, Math.Max(0, stateCount - batchIndex * batchSize));
        var readTimeoutMs = McpConstants.ComputeStateManagerReadTimeoutMs(statesInBatch, batchIndex);

        return await _pipeClient.CreateStateManagerAsync(
            managerName, groupName, groupProperty, states,
            bindNodePath, mode, confirmLargeBatch, batchIndex, batchSize, strategy,
            readTimeoutMs, totalStateCount, autoGenerateCount);
    }

    private async Task<string> ExecuteGetStatusAsync()
    {
        // 使用 GetConnectionStatusString() 获取连接状态而不尝试连接
        // 如果需要查询 Kanzi 内部状态，可以尝试连接
        var statusStr = _pipeClient.GetConnectionStatusString();
        try
        {
            // 尝试获取 Kanzi 内部状态（如果连接成功）
            if (_pipeClient.IsConnected)
            {
                var kanziStatus = await _pipeClient.GetStatusAsync();
                return $"{{\"pipe\": {statusStr}, \"kanzi\": {kanziStatus}}}";
            }
        }
        catch
        {
            // 连接失败时只返回管道状态
        }
        return $"{{\"pipe\": {statusStr}}}";
    }

    private async Task<string> ExecuteSearchNodesAsync(JsonElement args)
    {
        var searchText = args.TryGetProperty("searchText", out var st) ? st.GetString() ?? "" : "";
        var searchIn = new List<string> { "Name", "Path" };
        var caseSensitive = args.TryGetProperty("caseSensitive", out var cs) && cs.GetBoolean();

        if (args.TryGetProperty("searchIn", out var siEl))
        {
            searchIn = new List<string>();
            foreach (var item in siEl.EnumerateArray())
            {
                searchIn.Add(item.GetString() ?? "");
            }
        }

        if (string.IsNullOrEmpty(searchText))
            throw new ArgumentException("缺少 searchText 参数");

        return await _pipeClient.SearchNodesAsync(searchText, searchIn, caseSensitive);
    }

    #endregion

    #region 辅助方法

    private static NodeQueryFilter ParseQueryFilter(JsonElement? element)
    {
        var filter = new NodeQueryFilter();

        if (!element.HasValue)
            return filter;

        var e = element.Value;

        if (e.TryGetProperty("type", out var typeEl))
            filter.Type = typeEl.GetString();
        if (e.TryGetProperty("name", out var nameEl))
            filter.Name = nameEl.GetString();
        if (e.TryGetProperty("path", out var pathEl))
            filter.Path = pathEl.GetString();
        if (e.TryGetProperty("includeProperties", out var incPropsEl))
            filter.IncludeProperties = incPropsEl.GetBoolean();
        if (e.TryGetProperty("includeBindings", out var incBindingsEl))
            filter.IncludeBindings = incBindingsEl.GetBoolean();
        if (e.TryGetProperty("recursive", out var recursiveEl))
            filter.Recursive = recursiveEl.GetBoolean();
        if (e.TryGetProperty("limit", out var limitEl))
            filter.Limit = limitEl.GetInt32();

        return filter;
    }

    private static Dictionary<string, PropertyValue> ParseProperties(JsonElement? element)
    {
        var properties = new Dictionary<string, PropertyValue>();

        if (!element.HasValue)
            return properties;

        foreach (var prop in element.Value.EnumerateObject())
        {
            properties[prop.Name] = ParsePropertyValue(prop.Value);
        }

        return properties;
    }

    private static PropertyValue ParsePropertyValue(JsonElement element)
    {
        var value = new PropertyValue();

        // 简单值（string/number/bool/null/array）直接解析，不做 Object 字段探测
        if (element.ValueKind != JsonValueKind.Object)
        {
            value.Value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.ToString()
            };
            value.Type = element.ValueKind.ToString().ToLower();
            return value;
        }

        if (element.TryGetProperty("r", out _))
        {
            value.Type = "color";
            if (element.TryGetProperty("r", out var rEl)) value.R = rEl.GetSingle();
            if (element.TryGetProperty("g", out var gEl)) value.G = gEl.GetSingle();
            if (element.TryGetProperty("b", out var bEl)) value.B = bEl.GetSingle();
            if (element.TryGetProperty("a", out var aEl)) value.A = aEl.GetSingle();
            return value;
        }

        if (element.TryGetProperty("x", out _))
        {
            value.Type = "vector";
            if (element.TryGetProperty("x", out var xEl)) value.X = xEl.GetSingle();
            if (element.TryGetProperty("y", out var yEl)) value.Y = yEl.GetSingle();
            if (element.TryGetProperty("z", out var zEl)) value.Z = zEl.GetSingle();
            if (element.TryGetProperty("w", out var wEl)) value.W = wEl.GetSingle();
            return value;
        }

        value.Value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.ToString()
        };
        value.Type = element.ValueKind.ToString().ToLower();

        return value;
    }

    #endregion
}

/// <summary>
/// MCP 工具定义
/// </summary>
public class ToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public object InputSchema { get; set; } = new();
}
