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
        #region 状态

        public string GetStatus(JsonElement? args)
        {
            if (!HasStudio)
                return SafeSerialize(new
                {
                    success = true,
                    server = "KanziMcpPlugin",
                    version = "1.0.0",
                    projectOpen = false,
                    projectName = "",
                    kanziStudioConnected = false,
                    note = "Kanzi Studio not available"
                });

            // 实时检查项目状态，不依赖可能过期的 _isProjectOpen 字段
            var projectOpen = false;
            var projectName = "";
            var nodeCount = 0;

            try
            {
                var project = GetActiveProject();
                if (project != null)
                {
                    projectOpen = true;
                    projectName = SafeGetProperty(project, "Name") as string ?? "";

                    // 验证项目是否真的打开了（不是 null）
                    try
                    {
                        CountNodesRecursive(project, ref nodeCount, 0);
                    }
                    catch { nodeCount = -1; }
                }
            }
            catch { }

            // 更新内部状态以保持一致
            _isProjectOpen = projectOpen;
            _projectName = projectName;

            return SafeSerialize(new
            {
                success = true,
                server = "KanziMcpPlugin",
                version = "1.0.0",
                projectOpen,
                projectName,
                nodeCount,
                kanziStudioConnected = true,
                studioVersion = _studio!.Version,
                tcpPort = 9595
            });
        }

        private void CountNodesRecursive(object parent, ref int count, int depth)
        {
            if (depth > 25 || count > 10000) return;
            try
            {
                foreach (var child in GetChildren(parent))
                {
                    count++;
                    CountNodesRecursive(child, ref count, depth + 1);
                }
            }
            catch { }
        }

        #endregion
    }
}
