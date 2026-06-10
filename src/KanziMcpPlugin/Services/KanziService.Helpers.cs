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
        #region 辅助方法

        private void Log(string msg)
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

        /// <summary>
        /// 安全序列化 — 捕获序列化异常，降级到安全格式
        /// </summary>
        private string SafeSerialize(object obj)
        {
            try
            {
                return JsonSerializer.Serialize(obj, _jsonOptions);
            }
            catch (Exception ex)
            {
                Log($"SafeSerialize failed: {ex.Message}. Attempting fallback...");

                // 降级：将对象转为简单字典，确保所有值都可以序列化
                try
                {
                    var safeObj = MakeSafeForSerialization(obj);
                    return JsonSerializer.Serialize(safeObj, _jsonOptions);
                }
                catch (Exception ex2)
                {
                    Log($"SafeSerialize fallback also failed: {ex2.Message}");
                    return ErrorJson($"序列化失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 将对象转为 JSON 安全格式 — 递归处理所有不可序列化的值
        /// </summary>
        private object MakeSafeForSerialization(object obj)
        {
            if (obj == null) return null!;

            var type = obj.GetType();

            // 基本类型直接返回
            if (type.IsPrimitive || type.IsEnum || obj is string || obj is decimal ||
                obj is int || obj is long || obj is float || obj is double || obj is bool)
                return obj;

            // 字典：递归处理值
            if (obj is IDictionary dict)
            {
                var result = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dict)
                {
                    result[entry.Key.ToString() ?? ""] = entry.Value != null
                        ? MakeSafeForSerialization(entry.Value)
                        : null;
                }
                return result;
            }

            // 列表/集合：递归处理元素
            if (obj is IEnumerable enumerable && !(obj is string))
            {
                var list = new List<object?>();
                foreach (var item in enumerable)
                {
                    list.Add(item != null ? MakeSafeForSerialization(item) : null);
                }
                return list;
            }

            // 匿名类型和其他引用类型：转为字典
            var props = type.GetProperties();
            var safeDict = new Dictionary<string, object?>();
            foreach (var prop in props)
            {
                try
                {
                    var val = prop.GetValue(obj);
                    safeDict[prop.Name] = val != null ? MakeSafeForSerialization(val) : null;
                }
                catch
                {
                    safeDict[prop.Name] = $"[{prop.PropertyType.Name}]";
                }
            }
            return safeDict;
        }

        private string ErrorJson(string message)
        {
            return JsonSerializer.Serialize(new { error = message }, _jsonOptions);
        }

        private NodeFilter ParseNodeFilter(JsonElement? element, bool parseIncludeProperties = true)
        {
            var filter = new NodeFilter();
            if (!element.HasValue) return filter;
            var e = element.Value;
            if (e.ValueKind != JsonValueKind.Object) return filter;

            if (e.TryGetProperty("type", out var typeEl)) filter.Type = typeEl.GetString();
            if (e.TryGetProperty("name", out var nameEl)) filter.Name = nameEl.GetString();
            if (e.TryGetProperty("path", out var pathEl)) filter.Path = pathEl.GetString();
            if (parseIncludeProperties && e.TryGetProperty("includeProperties", out var ip))
                filter.IncludeProperties = ip.GetBoolean();
            if (e.TryGetProperty("recursive", out var r)) filter.Recursive = r.GetBoolean();
            if (e.TryGetProperty("limit", out var l)) filter.Limit = l.GetInt32();

            return filter;
        }

        private bool WildcardMatch(string input, string pattern)
        {
            if (pattern == "*") return true;
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private string GetNodeCategory(string type)
        {
            if (type.Contains("2D")) return "2D";
            if (type.Contains("3D")) return "3D";
            if (type.Contains("Text") || type.Contains("Image") || type.Contains("Button"))
                return "UI";
            return "Basic";
        }

        private Dictionary<string, PropertyMetadata> GetCommonPropertyMetadata(string nodeType)
        {
            var metadata = new Dictionary<string, PropertyMetadata>();

            // ========== 通用属性 ==========
            var commonProps = new[]
            {
                new PropertyMetadata { Name = "Opacity", Type = "float", DisplayName = "Opacity", Category = "Appearance", IsReadOnly = false },
                new PropertyMetadata { Name = "IsVisible", Type = "bool", DisplayName = "Visibility", Category = "Appearance", IsReadOnly = false },
                new PropertyMetadata { Name = "Position", Type = "vector2", DisplayName = "Position", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Size", Type = "vector2", DisplayName = "Size", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Width", Type = "float", DisplayName = "Width", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Height", Type = "float", DisplayName = "Height", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Scale", Type = "vector2", DisplayName = "Scale", Category = "Transform", IsReadOnly = false },
                new PropertyMetadata { Name = "Rotation", Type = "float", DisplayName = "Rotation", Category = "Transform", IsReadOnly = false },
                new PropertyMetadata { Name = "HorizontalAlignment", Type = "enum", DisplayName = "Horizontal Alignment", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "VerticalAlignment", Type = "enum", DisplayName = "Vertical Alignment", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Margin", Type = "vector4", DisplayName = "Margin", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Padding", Type = "vector4", DisplayName = "Padding", Category = "Layout", IsReadOnly = false },
                new PropertyMetadata { Name = "Name", Type = "string", DisplayName = "Node Name", Category = "General", IsReadOnly = false },
                new PropertyMetadata { Name = "Enabled", Type = "bool", DisplayName = "Enabled", Category = "General", IsReadOnly = false },
                new PropertyMetadata { Name = "PageHost", Type = "NodeReference", DisplayName = "Page Host", Category = "Navigation", IsReadOnly = false },
                new PropertyMetadata { Name = "DataContext", Type = "object", DisplayName = "Data Context", Category = "Data", IsReadOnly = false },
            };

            foreach (var prop in commonProps)
                metadata[prop.Name] = prop;

            // ========== 节点类型特定属性 ==========
            switch (nodeType)
            {
                case "TextBlock2D":
                    metadata["Text"] = new PropertyMetadata { Name = "Text", Type = "string", DisplayName = "Text Content", Category = "Text", IsReadOnly = false };
                    metadata["FontColor"] = new PropertyMetadata { Name = "FontColor", Type = "color", DisplayName = "Font Color", Category = "Text", IsReadOnly = false };
                    metadata["FontSize"] = new PropertyMetadata { Name = "FontSize", Type = "float", DisplayName = "Font Size", Category = "Text", IsReadOnly = false };
                    metadata["FontWeight"] = new PropertyMetadata { Name = "FontWeight", Type = "enum", DisplayName = "Font Weight", Category = "Text", IsReadOnly = false };
                    metadata["FontStyle"] = new PropertyMetadata { Name = "FontStyle", Type = "enum", DisplayName = "Font Style", Category = "Text", IsReadOnly = false };
                    metadata["TextAlignment"] = new PropertyMetadata { Name = "TextAlignment", Type = "enum", DisplayName = "Text Alignment", Category = "Text", IsReadOnly = false };
                    metadata["HorizontalContentAlignment"] = new PropertyMetadata { Name = "HorizontalContentAlignment", Type = "enum", DisplayName = "Horizontal Content Alignment", Category = "Text", IsReadOnly = false };
                    metadata["VerticalContentAlignment"] = new PropertyMetadata { Name = "VerticalContentAlignment", Type = "enum", DisplayName = "Vertical Content Alignment", Category = "Text", IsReadOnly = false };
                    metadata["TextTrimming"] = new PropertyMetadata { Name = "TextTrimming", Type = "enum", DisplayName = "Text Trimming", Category = "Text", IsReadOnly = false };
                    metadata["WordWrap"] = new PropertyMetadata { Name = "WordWrap", Type = "bool", DisplayName = "Word Wrap", Category = "Text", IsReadOnly = false };
                    break;

                case "Image2D":
                    metadata["Texture"] = new PropertyMetadata { Name = "Texture", Type = "resource", DisplayName = "Texture", Category = "Appearance", IsReadOnly = false };
                    metadata["BrushColor"] = new PropertyMetadata { Name = "BrushColor", Type = "color", DisplayName = "Brush Color", Category = "Appearance", IsReadOnly = false };
                    metadata["Stretch"] = new PropertyMetadata { Name = "Stretch", Type = "enum", DisplayName = "Stretch Mode", Category = "Appearance", IsReadOnly = false };
                    metadata["Tile"] = new PropertyMetadata { Name = "Tile", Type = "bool", DisplayName = "Tile", Category = "Appearance", IsReadOnly = false };
                    break;

                case "RectangleNode2D":
                    metadata["FillColor"] = new PropertyMetadata { Name = "FillColor", Type = "color", DisplayName = "Fill Color", Category = "Appearance", IsReadOnly = false };
                    metadata["BorderColor"] = new PropertyMetadata { Name = "BorderColor", Type = "color", DisplayName = "Border Color", Category = "Appearance", IsReadOnly = false };
                    metadata["BorderWidth"] = new PropertyMetadata { Name = "BorderWidth", Type = "float", DisplayName = "Border Width", Category = "Appearance", IsReadOnly = false };
                    metadata["CornerRadius"] = new PropertyMetadata { Name = "CornerRadius", Type = "vector4", DisplayName = "Corner Radius", Category = "Appearance", IsReadOnly = false };
                    break;

                case "Button2D":
                    metadata["Text"] = new PropertyMetadata { Name = "Text", Type = "string", DisplayName = "Button Text", Category = "Content", IsReadOnly = false };
                    metadata["FontColor"] = new PropertyMetadata { Name = "FontColor", Type = "color", DisplayName = "Font Color", Category = "Content", IsReadOnly = false };
                    metadata["FontSize"] = new PropertyMetadata { Name = "FontSize", Type = "float", DisplayName = "Font Size", Category = "Content", IsReadOnly = false };
                    metadata["Background"] = new PropertyMetadata { Name = "Background", Type = "resource", DisplayName = "Background", Category = "Appearance", IsReadOnly = false };
                    metadata["PressedBackground"] = new PropertyMetadata { Name = "PressedBackground", Type = "resource", DisplayName = "Pressed Background", Category = "Appearance", IsReadOnly = false };
                    break;

                case "ProgressBar2D":
                    metadata["Minimum"] = new PropertyMetadata { Name = "Minimum", Type = "float", DisplayName = "Minimum", Category = "Value", IsReadOnly = false };
                    metadata["Maximum"] = new PropertyMetadata { Name = "Maximum", Type = "float", DisplayName = "Maximum", Category = "Value", IsReadOnly = false };
                    metadata["Value"] = new PropertyMetadata { Name = "Value", Type = "float", DisplayName = "Current Value", Category = "Value", IsReadOnly = false };
                    break;

                case "Slider2D":
                    metadata["Minimum"] = new PropertyMetadata { Name = "Minimum", Type = "float", DisplayName = "Minimum", Category = "Value", IsReadOnly = false };
                    metadata["Maximum"] = new PropertyMetadata { Name = "Maximum", Type = "float", DisplayName = "Maximum", Category = "Value", IsReadOnly = false };
                    metadata["Value"] = new PropertyMetadata { Name = "Value", Type = "float", DisplayName = "Current Value", Category = "Value", IsReadOnly = false };
                    metadata["Step"] = new PropertyMetadata { Name = "Step", Type = "float", DisplayName = "Step", Category = "Value", IsReadOnly = false };
                    break;

                case "EmptyNode2D":
                case "Node2D":
                    // 这些节点只有通用属性
                    break;

                case "Viewport2D":
                    metadata["Camera"] = new PropertyMetadata { Name = "Camera", Type = "NodeReference", DisplayName = "Camera", Category = "Rendering", IsReadOnly = false };
                    break;
            }

            return metadata;
        }

        #endregion
    }
}
