// NodeModels.cs
//
// 文件作用: Kanzi 节点数据模型（MCP Server 端）
// 关键类: KanziNode, NodeQueryFilter, NodeTreeOptions
// 主要职责:
//   1. KanziNode          : 节点基础信息（path/name/type/properties/bindings）
//   2. NodeQueryFilter    : 节点查询过滤条件（type/name/path/includeProperties等）
//   3. NodeTreeOptions    : 节点树选项（maxDepth/includeProperties等）
//   4. 所有模型使用 [JsonPropertyName] 控制 JSON 序列化字段名
// 依赖: System.Text.Json（.NET 10 内置）
// 说明: 这些模型运行在 MCP Server 进程（.NET 10），通过 JSON 序列化后
//       经 Named Pipe 传递到 Kanzi Plugin 端（.NET 4.8）

using System.Text.Json.Serialization;

namespace KanziMcpServer.Models;

#region 节点基础模型

/// <summary>
/// Kanzi 节点信息
/// </summary>
public class KanziNode
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("children")]
    public List<KanziNode> Children { get; set; } = new();

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; set; } = new();

    [JsonPropertyName("bindings")]
    public List<KanziBinding>? Bindings { get; set; }

    [JsonPropertyName("metadata")]
    public NodeMetadata? Metadata { get; set; }
}

/// <summary>
/// 节点元数据
/// </summary>
public class NodeMetadata
{
    [JsonPropertyName("isPrefab")]
    public bool IsPrefab { get; set; }

    [JsonPropertyName("isInstance")]
    public bool IsInstance { get; set; }

    [JsonPropertyName("prefabPath")]
    public string? PrefabPath { get; set; }

    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; set; } = true;

    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; set; }
}

/// <summary>
/// 节点查询过滤器
/// </summary>
public class NodeQueryFilter
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("includeProperties")]
    public bool IncludeProperties { get; set; } = false;

    [JsonPropertyName("includeBindings")]
    public bool IncludeBindings { get; set; } = false;

    [JsonPropertyName("recursive")]
    public bool Recursive { get; set; } = true;

    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; } = 10;

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 1000;
}

/// <summary>
/// 节点查询结果
/// </summary>
public class NodeQueryResult
{
    [JsonPropertyName("nodes")]
    public List<KanziNode> Nodes { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count => Nodes.Count;

    [JsonPropertyName("query")]
    public NodeQueryFilter? Query { get; set; }

    [JsonPropertyName("totalMatched")]
    public int TotalMatched { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }
}

#endregion

#region 节点树模型

/// <summary>
/// 节点树请求
/// </summary>
public class NodeTreeRequest
{
    [JsonPropertyName("rootPath")]
    public string? RootPath { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; } = 3;

    [JsonPropertyName("includeProperties")]
    public bool IncludeProperties { get; set; } = false;
}

/// <summary>
/// 节点类型信息
/// </summary>
public class NodeTypeInfo
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "General";

    [JsonPropertyName("isAbstract")]
    public bool IsAbstract { get; set; }

    [JsonPropertyName("baseTypes")]
    public List<string> BaseTypes { get; set; } = new();

    [JsonPropertyName("propertyCount")]
    public int PropertyCount { get; set; }
}

/// <summary>
/// 节点类型列表结果
/// </summary>
public class NodeTypeListResult
{
    [JsonPropertyName("nodeTypes")]
    public List<NodeTypeInfo> NodeTypes { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count => NodeTypes.Count;

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();
}

#endregion

#region 绑定模型

/// <summary>
/// Kanzi 数据绑定
/// </summary>
public class KanziBinding
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("sourceProperty")]
    public string SourceProperty { get; set; } = string.Empty;

    [JsonPropertyName("targetProperty")]
    public string TargetProperty { get; set; } = string.Empty;

    [JsonPropertyName("dataSource")]
    public string DataSource { get; set; } = string.Empty;

    [JsonPropertyName("dataSourcePath")]
    public string DataSourcePath { get; set; } = string.Empty;

    [JsonPropertyName("converter")]
    public string? Converter { get; set; }

    [JsonPropertyName("mode")]
    public BindingMode Mode { get; set; } = BindingMode.OneWay;

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// 绑定模式
/// </summary>
public enum BindingMode
{
    OneWay,
    TwoWay,
    OneWayToSource,
    Command
}

#endregion

#region 属性模型

/// <summary>
/// 属性值
/// </summary>
public class PropertyValue
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "unknown";

    [JsonPropertyName("value")]
    public object? Value { get; set; }

    // 颜色
    [JsonPropertyName("r")]
    public float? R { get; set; }

    [JsonPropertyName("g")]
    public float? G { get; set; }

    [JsonPropertyName("b")]
    public float? B { get; set; }

    [JsonPropertyName("a")]
    public float? A { get; set; }

    // 向量
    [JsonPropertyName("x")]
    public float? X { get; set; }

    [JsonPropertyName("y")]
    public float? Y { get; set; }

    [JsonPropertyName("z")]
    public float? Z { get; set; }

    [JsonPropertyName("w")]
    public float? W { get; set; }

    public bool IsColor => R.HasValue && G.HasValue && B.HasValue;
    public bool IsVector => X.HasValue || Y.HasValue || Z.HasValue || W.HasValue;
}

/// <summary>
/// 属性变更记录
/// </summary>
public class PropertyChange
{
    [JsonPropertyName("node")]
    public string Node { get; set; } = string.Empty;

    [JsonPropertyName("property")]
    public string Property { get; set; } = string.Empty;

    [JsonPropertyName("oldValue")]
    public object? OldValue { get; set; }

    [JsonPropertyName("newValue")]
    public object? NewValue { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// 批量属性设置请求
/// </summary>
public class BatchSetPropertyRequest
{
    [JsonPropertyName("filter")]
    public NodeQueryFilter Filter { get; set; } = new();

    [JsonPropertyName("properties")]
    public Dictionary<string, PropertyValue> Properties { get; set; } = new();

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "preview";

    [JsonPropertyName("ignoreReadOnly")]
    public bool IgnoreReadOnly { get; set; } = false;
}

/// <summary>
/// 批量属性设置结果
/// </summary>
public class BatchSetPropertyResult
{
    [JsonPropertyName("affectedNodes")]
    public List<string> AffectedNodes { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count => AffectedNodes.Count;

    [JsonPropertyName("changes")]
    public List<PropertyChange> Changes { get; set; } = new();

    [JsonPropertyName("errors")]
    public List<PropertyChange> Errors { get; set; } = new();

    [JsonPropertyName("skipped")]
    public List<PropertyChange> Skipped { get; set; } = new();

    [JsonPropertyName("summary")]
    public string Summary => $"Modified {Changes.Count}, Failed {Errors.Count}, Skipped {Skipped.Count}";
}

#endregion

#region 状态模型

/// <summary>
/// MCP 服务器状态
/// </summary>
public class ServerStatus
{
    [JsonPropertyName("server")]
    public string Server => McpConstants.ServerName;

    [JsonPropertyName("version")]
    public string Version => McpConstants.ServerVersion;

    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion => McpConstants.ProtocolVersion;

    [JsonPropertyName("kanziConnected")]
    public bool KanziConnected { get; set; }

    [JsonPropertyName("projectOpen")]
    public bool ProjectOpen { get; set; }

    [JsonPropertyName("projectName")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("uptime")]
    public TimeSpan Uptime { get; set; }
}

/// <summary>
/// 连接状态
/// </summary>
public class ConnectionStatus
{
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("pipeName")]
    public string PipeName { get; set; } = McpConstants.DefaultPipeName;

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }
}

#endregion
