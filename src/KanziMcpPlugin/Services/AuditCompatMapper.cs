using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace KanziMcpPlugin.Services
{
    /// <summary>
    /// Maps kanzi_doctor_resource output to the legacy kanzi_audit_resource_references schema.
    /// </summary>
    internal static class AuditCompatMapper
    {
        public const string RemovedInVersion = "2.0";

        public static string BuildLocalizationDeprecatedJson()
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                deprecated = true,
                removedIn = RemovedInVersion,
                message = "Localization audit was removed: core translation checks were never implemented reliably.",
                alternatives = new[]
                {
                    "kanzi_search_nodes (searchIn: Text)",
                    "kanzi_query_nodes"
                }
            });
        }

        public static JsonElement BuildDoctorArgs(bool checkUnused, bool checkBroken, bool checkOrphaned)
        {
            // checkOrphaned uses the same unused scan in doctor; no separate flag needed.
            _ = checkOrphaned;
            var payload = new
            {
                checkImages = checkUnused,
                checkTextures = checkUnused,
                checkBroken,
                resourceFolders = new[] { "Textures" }
            };
            return JsonSerializer.SerializeToElement(payload);
        }

        public static string MapDoctorJsonToResourceReferencesCompat(string doctorJson, bool checkOrphaned)
        {
            using var doc = JsonDocument.Parse(doctorJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl))
            {
                return JsonSerializer.Serialize(new { error = errEl.GetString() });
            }

            if (root.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.False)
            {
                return doctorJson;
            }

            var unusedResources = new List<Dictionary<string, object?>>();
            AppendUnused(unusedResources, root, "unusedImages");
            AppendUnused(unusedResources, root, "unusedTextures");

            var brokenReferences = new List<Dictionary<string, object?>>();
            if (root.TryGetProperty("brokenReferences", out var brokenEl) && brokenEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in brokenEl.EnumerateArray())
                {
                    brokenReferences.Add(JsonElementToDict(item));
                }
            }

            var orphanedResources = new List<Dictionary<string, object?>>();
            if (checkOrphaned)
            {
                foreach (var res in unusedResources)
                {
                    res.TryGetValue("type", out var resType);
                    res.TryGetValue("path", out var resPath);
                    res.TryGetValue("name", out var resName);
                    orphanedResources.Add(new Dictionary<string, object?>
                    {
                        ["type"] = resType,
                        ["path"] = resPath,
                        ["name"] = resName,
                        ["message"] = $"{resType} '{resName}' is not referenced by any node"
                    });
                }
            }

            var detectedTypes = unusedResources
                .Select(r => r.TryGetValue("type", out var t) ? t?.ToString() ?? "" : "")
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .Take(30)
                .ToList();

            var totalReferences = root.TryGetProperty("detectedReferenceCount", out var refEl) && refEl.TryGetInt32(out var rc)
                ? rc
                : 0;

            var recommendations = new List<string>();
            if (unusedResources.Count == 0 && brokenReferences.Count == 0)
            {
                recommendations.Add($"Scanned resources via kanzi_doctor_resource; no unused or broken items found.");
            }
            if (unusedResources.Count > 0)
                recommendations.Add($"Found {unusedResources.Count} unused resources — consider removing them to reduce project size.");
            if (brokenReferences.Count > 0)
                recommendations.Add($"Found {brokenReferences.Count} broken references that may cause runtime errors.");

            return JsonSerializer.Serialize(new
            {
                success = true,
                deprecated = true,
                redirectTo = "kanzi_doctor_resource",
                unusedResources,
                brokenReferences,
                orphanedResources,
                summary = new
                {
                    totalResources = unusedResources.Count + totalReferences,
                    totalUnused = unusedResources.Count,
                    totalBroken = brokenReferences.Count,
                    totalOrphaned = orphanedResources.Count,
                    totalReferences,
                    detectedResourceTypes = detectedTypes
                },
                recommendations
            });
        }

        private static void AppendUnused(List<Dictionary<string, object?>> target, JsonElement root, string arrayName)
        {
            if (!root.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in arr.EnumerateArray())
            {
                target.Add(new Dictionary<string, object?>
                {
                    ["type"] = GetStringProp(item, "type"),
                    ["path"] = GetStringProp(item, "path"),
                    ["name"] = GetStringProp(item, "name")
                });
            }
        }

        private static string? GetStringProp(JsonElement el, string name)
        {
            return el.TryGetProperty(name, out var p) ? p.GetString() : null;
        }

        private static Dictionary<string, object?> JsonElementToDict(JsonElement el)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var prop in el.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.ToString()
                };
            }
            return dict;
        }
    }
}
