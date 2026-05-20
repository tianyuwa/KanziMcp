# 构建说明

## 当前状态 (2026-05-16)

### test_mcp_client.py
- ✅ 已更新
- ✅ 已上传至 oss://mcpkanzipublish/outgoing/test_mcp_client.py
- 新增 6 个工具的自动化测试

### monitor_feishu.ps1
- ✅ 已创建
- ✅ 已上传至 oss://mcpkanzipublish/outgoing/monitor_feishu.ps1
- 功能: Build → Test → Upload

### KanziMcpServer.exe
- ✅ **编译成功** (2026-05-16 13:57)
- ✅ **已上传至 OSS** oss://mcpkanzipublish/incoming/Build_MCP/
- ✅ latest_build.txt 已更新
- 使用 .NET 10 --no-restore 编译（绕过 NuGet bug）

## 下一步

在 Kanzi 机器上运行:

```powershell
# 1. 下载最新脚本
ossutil64 cp oss://mcpkanzipublish/outgoing/monitor_feishu.ps1 .\monitor_feishu.ps1

# 2. 运行完整流程（编译 + 测试 + 上传）
.\monitor_feishu.ps1

# 或分步骤运行:
.\monitor_feishu.ps1 -Step Build   # 仅编译
.\monitor_feishu.ps1 -Step Test    # 仅测试
.\monitor_feishu.ps1 -Step Upload  # 仅上传
```

## 已上传的文件

| 文件 | OSS 路径 | 状态 |
|------|----------|------|
| test_mcp_client.py | oss://mcpkanzipublish/outgoing/test_mcp_client.py | ✅ |
| monitor_feishu.ps1 | oss://mcpkanzipublish/outgoing/monitor_feishu.ps1 | ✅ |
| KanziMcpServer.exe | - | ❌ 需要手动编译 |
