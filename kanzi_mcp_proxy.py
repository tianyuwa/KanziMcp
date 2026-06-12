#!/usr/bin/env python3
"""
Kanzi MCP Proxy — 本地电脑上运行，作为 Claude Code 的 MCP Server 入口

作用: 接收 Claude Code 的 MCP stdio 请求 → 上传到 OSS → 轮询结果 → 返回给 Claude Code

Claude Code 通过 stdin/stdout 与本脚本通信（JSON-RPC over stdio）。
本脚本通过 OSS (claudemcp bucket) 与 Kanzi 电脑上的 oss_bridge_daemon.py 通信。

用法:
  Claude Code 启动时会自动 fork 此脚本（通过 MCP 配置）。
  也可手动测试：
    echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"claude-code","version":"1.0.0"},"capabilities":{}}}' | python kanzi_mcp_proxy.py

环境变量:
  OSS_ACCESS_KEY_ID     - 阿里云 AccessKey ID
  OSS_ACCESS_KEY_SECRET - 阿里云 AccessKey Secret
  OSS_ENDPOINT          - OSS endpoint（默认 oss-cn-beijing.aliyuncs.com）
  OSS_BUCKET            - OSS bucket 名称（默认 claudemcp）
  POLL_INTERVAL         - 轮询间隔秒数（默认 1.0）
  REQUEST_TIMEOUT       - 等待响应超时秒数（默认 660，需大于 Kanzi 单批 apply 超时）
"""

import oss2
import json
import sys
import os
import time
import uuid
import threading

# ============ 配置 ============
ENDPOINT = os.environ.get('OSS_ENDPOINT', 'oss-cn-beijing.aliyuncs.com')
ACCESS_KEY_ID = os.environ.get('OSS_ACCESS_KEY_ID', '')
ACCESS_KEY_SECRET = os.environ.get('OSS_ACCESS_KEY_SECRET', '')
BUCKET_NAME = os.environ.get('OSS_BUCKET', 'claudemcp')

POLL_INTERVAL = float(os.environ.get('POLL_INTERVAL', '1.0'))
REQUEST_TIMEOUT = int(os.environ.get('REQUEST_TIMEOUT', '660'))

REQUEST_PREFIX = 'requests/'
RESPONSE_PREFIX = 'responses/'

# Heartbeat check
HEARTBEAT_CHECK_INTERVAL = 5  # 每 5 次请求检查一次心跳

# Server capabilities
SERVER_INFO = {
    "name": "kanzi-mcp-proxy",
    "version": "1.0.0"
}

SERVER_CAPABILITIES = {
    "tools": {}
}

# 工具列表 — 跟 KanziMcpServer 暴露的工具保持一致
# 注意：这里只是让 Claude Code 知道有哪些工具可用
# 实际执行在 Kanzi 电脑上
TOOLS = [
    # ========== 查询工具 ==========
    {
        "name": "kanzi_query_nodes",
        "description": "Query Kanzi nodes by type, name, or path. Returns detailed node information including properties if requested.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "type": {"type": "string", "description": "Node type filter (e.g. Text Block 2D, Image, TextBlock2D)"},
                "name": {"type": "string", "description": "Node name filter, supports wildcards (*). Example: '*标题*' matches nodes containing '标题'"},
                "path": {"type": "string", "description": "Node path prefix. Example: Screens/Screen/RootPage"},
                "includeProperties": {"type": "boolean", "description": "Include node properties in response", "default": False},
                "includeBindings": {"type": "boolean", "description": "Include data binding information", "default": False},
                "recursive": {"type": "boolean", "description": "Search recursively", "default": True},
                "limit": {"type": "integer", "description": "Maximum number of results", "default": 1000}
            }
        }
    },
    {
        "name": "kanzi_get_node_tree",
        "description": "Get the hierarchical node tree structure starting from a specified root node.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "rootPath": {"type": "string", "description": "Root node path. Leave empty for project root."},
                "depth": {"type": "integer", "description": "Maximum depth to traverse", "default": 3},
                "includeProperties": {"type": "boolean", "description": "Include properties in response", "default": False}
            }
        }
    },
    {
        "name": "kanzi_list_node_types",
        "description": "List all unique node types used in the project",
        "inputSchema": {
            "type": "object",
            "properties": {}
        }
    },
    {
        "name": "kanzi_get_binding_info",
        "description": "Get detailed data binding information for a specific node.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Full node path (e.g. Screens/Screen/RootPage/Viewport 2D/Text Block 2D)"},
                "includeMetadata": {"type": "boolean", "description": "Include binding metadata", "default": False}
            },
            "required": ["path"]
        }
    },

    # ========== 属性操作工具 ==========
    {
        "name": "kanzi_set_node_property",
        "description": "Set a single property on a node. Use mode='preview' to check changes before applying.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Full node path (e.g. Screens/Screen/RootPage/Viewport 2D/Text Block 2D)"},
                "property": {"type": "string", "description": "Property name (e.g. TextConcept.Text, LayoutHorizontalAlignment)"},
                "value": {"type": "object", "description": "Property value — can be a string, number, boolean, color object {r,g,b,a}, or vector object {x,y,z,w}"},
                "mode": {"type": "string", "description": "'preview' checks without applying, 'apply' makes the change", "default": "preview", "enum": ["preview", "apply"]},
                "force": {"type": "boolean", "description": "Force set even if read-only", "default": False}
            },
            "required": ["path", "property", "value"]
        }
    },
    {
        "name": "kanzi_batch_set_property",
        "description": "Batch set properties on multiple nodes matching a filter. Always preview first!",
        "inputSchema": {
            "type": "object",
            "properties": {
                "filter": {"type": "object", "description": "Node filter criteria (e.g. {\"type\": \"Text Block 2D\", \"recursive\": True})"},
                "properties": {"type": "object", "description": "Properties to set as key-value pairs"},
                "mode": {"type": "string", "description": "'preview' or 'apply'", "default": "preview", "enum": ["preview", "apply"]},
                "ignoreReadOnly": {"type": "boolean", "description": "Skip read-only properties", "default": False}
            },
            "required": ["filter", "properties"]
        }
    },
    {
        "name": "kanzi_get_property_metadata",
        "description": "Get property metadata for a node type (data type, read-only status, default value).",
        "inputSchema": {
            "type": "object",
            "properties": {
                "nodeType": {"type": "string", "description": "Node type name (e.g. Text Block 2D, Node2D, Image2D)"}
            },
            "required": ["nodeType"]
        }
    },

    # ========== 审计工具 ==========
    {
        "name": "kanzi_audit_bindings",
        "description": "Audit all data bindings in the project. Find missing data sources, orphan bindings, and priority conflicts.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Scope path (empty for full project)"},
                "checkPriority": {"type": "boolean", "description": "Check for priority conflicts", "default": True},
                "findOrphans": {"type": "boolean", "description": "Find orphan bindings", "default": True}
            }
        }
    },
    {
        "name": "kanzi_audit_localization",
        "description": "Audit localization coverage. Find missing translations and inconsistent text keys.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "languages": {"type": "array", "items": {"type": "string"}, "description": "Target languages to check"},
                "includeUntranslated": {"type": "boolean", "description": "Include untranslated text nodes", "default": True}
            }
        }
    },
    {
        "name": "kanzi_audit_project_structure",
        "description": "Audit project structure for naming conventions and organization best practices.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "namingPattern": {"type": "string", "description": "Regex pattern for naming convention (e.g. '^[A-Z][a-zA-Z0-9 _]*$')"},
                "checkDepth": {"type": "boolean", "description": "Check for excessively deep nesting", "default": True},
                "checkNaming": {"type": "boolean", "description": "Check naming conventions", "default": True}
            }
        }
    },
    {
        "name": "kanzi_audit_resource_references",
        "description": "Audit resource references — find unused, broken, or orphaned resources (images, textures, materials).",
        "inputSchema": {
            "type": "object",
            "properties": {
                "checkUnused": {"type": "boolean", "description": "Find unused resources", "default": True},
                "checkBroken": {"type": "boolean", "description": "Find broken/missing resource references", "default": True},
                "checkOrphaned": {"type": "boolean", "description": "Find orphaned resource files", "default": True}
            }
        }
    },

    # ========== 节点创建与删除 ==========
    {
        "name": "kanzi_create_node",
        "description": "Create a new node under a parent node. Use this to add nodes like Empty Node 2D, Text Block 2D, etc.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "parentPath": {"type": "string", "description": "Parent node path where the new node will be created"},
                "nodeType": {"type": "string", "description": "Node type (e.g. Empty Node 2D, Text Block 2D, Text Block 3D, Image2D)"},
                "nodeName": {"type": "string", "description": "Name for the new node (optional)"},
                "properties": {"type": "object", "description": "Initial properties to set on the new node (optional)"}
            },
            "required": ["parentPath", "nodeType"]
        }
    },
    {
        "name": "kanzi_delete_node",
        "description": "Delete a node. Use preview/dry-run mode first to see what will be deleted. CAUTION: This deletes the node and all its children!",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Full path of the node to delete"},
                "mode": {"type": "string", "description": "'preview' or 'dry-run' to see what will be deleted without actually deleting, 'apply' to delete", "default": "apply", "enum": ["preview", "dry-run", "apply"]}
            },
            "required": ["path"]
        }
    },

    # ========== 资源导入 ==========
    {
        "name": "kanzi_import_image",
        "description": "Import an image file into the Kanzi resource library (Textures folder). Supported formats: PNG, JPG, BMP, etc.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "filePath": {"type": "string", "description": "Full path to the image file on your computer"},
                "resourceName": {"type": "string", "description": "Optional name for the imported resource"},
                "targetFolder": {"type": "string", "description": "Target resource folder (default: 'Textures')", "default": "Textures"}
            },
            "required": ["filePath"]
        }
    },
    {
        "name": "kanzi_import_fbx",
        "description": "Import a 3D model (FBX format) into the Kanzi resource library (Meshes folder).",
        "inputSchema": {
            "type": "object",
            "properties": {
                "filePath": {"type": "string", "description": "Full path to the FBX file on your computer"},
                "resourceName": {"type": "string", "description": "Optional name for the imported resource"},
                "targetFolder": {"type": "string", "description": "Target resource folder (default: 'Meshes')", "default": "Meshes"}
            },
            "required": ["filePath"]
        }
    },

    # ========== 资源诊断 ==========
    {
        "name": "kanzi_doctor_resource",
        "description": "Diagnose resource usage in the project. Find unused Image and Texture resources that can be safely removed to reduce project size.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "checkImages": {"type": "boolean", "description": "Check for unused images", "default": True},
                "checkTextures": {"type": "boolean", "description": "Check for unused textures", "default": True}
            }
        }
    },

    # ========== 自定义属性工具 ==========
    {
        "name": "kanzi_upsert_custom_enum_property",
        "description": "Create or update a Custom Enum Property in the project. If a property with the same name already exists and is a CustomEnumProperty, it updates the options/displayName/category. If it exists but is a different type, it deletes and recreates. If it does not exist, it creates a new one.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "Property name (e.g., 'WarningValue', 'PopState')"},
                "options": {"type": "array", "description": "Array of { name: string, value: int } objects defining the enum options"},
                "displayName": {"type": "string", "description": "Display name for the property (default: '<Name>-name')"},
                "category": {"type": "string", "description": "Category for the property (default: '')"},
                "mode": {"type": "string", "enum": ["preview", "apply"], "description": "'preview' checks without applying, 'apply' makes the change", "default": "preview"}
            },
            "required": ["name", "options"]
        }
    },
    {
        "name": "kanzi_create_state_manager",
        "description": "Create a State Manager with StateGroup, States, and StateObjects. Supports batched creation for large state counts.\n\nUsage order:\n1. First call kanzi_upsert_custom_enum_property to ensure the groupProperty exists\n2. Then call kanzi_create_state_manager with mode=preview to see the batch plan\n3. Large jobs: autoGenerateCount + 1 template (use {0} in strings), batchSize=12..16, loop batchIndex with mode=apply\n4. Or per-batch states with totalStateCount\n5. If stateCount > 200, must set confirmLargeBatch=true\n6. Not recommended to exceed 500 states per group; split into multiple StateGroups instead",
        "inputSchema": {
            "type": "object",
            "properties": {
                "managerName": {"type": "string", "description": "Name of the State Manager"},
                "groupName": {"type": "string", "description": "Name of the State Group"},
                "groupProperty": {"type": "string", "description": "Property name for the group controller (must be a CustomEnumProperty)"},
                "states": {"type": "array", "description": "State definitions, or one template when autoGenerateCount is set ({0} = index)"},
                "bindNodePath": {"type": "string", "description": "Path of the node to bind the StateManager to (e.g., 'Screens/Screen/RootPage/Viewport')"},
                "mode": {"type": "string", "enum": ["preview", "apply"], "description": "'preview' or 'apply'", "default": "preview"},
                "confirmLargeBatch": {"type": "boolean", "description": "Required true when stateCount > 200", "default": False},
                "batchIndex": {"type": "integer", "description": "Batch index for incremental apply (0-based)", "default": 0},
                "batchSize": {"type": "integer", "description": "States per batch (max 16 with autoGenerate/totalStateCount; default 12)", "default": 12},
                "totalStateCount": {"type": "integer", "description": "Total states when sending per-batch subsets (optional)", "default": 0},
                "autoGenerateCount": {"type": "integer", "description": "Generate N states from first template; use with batchIndex (optional)", "default": 0},
                "strategy": {"type": "string", "enum": ["auto", "clone", "direct"], "description": "Creation strategy: 'auto', 'clone', or 'direct'", "default": "auto"}
            },
            "required": ["managerName", "groupName", "groupProperty", "states"]
        }
    },

    # ========== 实用工具 ==========
    {
        "name": "kanzi_get_status",
        "description": "Get MCP server and Kanzi connection status.",
        "inputSchema": {
            "type": "object",
            "properties": {}
        }
    },
    {
        "name": "kanzi_search_nodes",
        "description": "Search nodes by name, path, type, or text content. Default searches Name and Path.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "searchText": {"type": "string", "description": "Text to search for in node properties"},
                "searchIn": {"type": "array", "items": {"type": "string"}, "description": "Properties to search in (default: ['Name', 'Path'], options: 'Name', 'Path', 'Type', 'Text')"},
                "caseSensitive": {"type": "boolean", "description": "Case sensitive search", "default": False}
            },
            "required": ["searchText"]
        }
    }
]

# 用于 stderr 日志
def log(msg):
    print(msg, file=sys.stderr, flush=True)


class OSSMcpProxy:
    """MCP Proxy: stdio <-> OSS bridge"""

    def __init__(self):
        auth = oss2.Auth(ACCESS_KEY_ID, ACCESS_KEY_SECRET)
        self.bucket = oss2.Bucket(auth, ENDPOINT, BUCKET_NAME)
        self.request_count = 0

    def send_via_oss(self, method, params=None, req_id=1):
        """通过 OSS 发送请求并等待响应"""
        # 生成唯一 ID
        oss_id = str(uuid.uuid4())[:8]

        # 构造 JSON-RPC 请求
        rpc_request = {
            "jsonrpc": "2.0",
            "id": req_id,
            "method": method
        }
        if params is not None:
            rpc_request["params"] = params

        # 上传请求到 OSS
        req_key = f"{REQUEST_PREFIX}mcp_req_{oss_id}.json"
        req_data = json.dumps(rpc_request, ensure_ascii=False).encode('utf-8')

        try:
            self.bucket.put_object(req_key, req_data)
        except Exception as e:
            log(f"OSS upload failed: {e}")
            return None

        # 轮询等待响应
        resp_key = f"{RESPONSE_PREFIX}mcp_resp_{oss_id}.json"
        deadline = time.time() + REQUEST_TIMEOUT

        while time.time() < deadline:
            try:
                self.bucket.get_object(resp_key)
                # 文件存在，下载响应
                resp_obj = self.bucket.get_object(resp_key)
                resp_data = resp_obj.read().decode('utf-8')
                response = json.loads(resp_data)

                # 清理 OSS 上的响应文件
                try:
                    self.bucket.delete_object(resp_key)
                except Exception:
                    pass

                return response

            except oss2.exceptions.NoSuchKey:
                # 响应还没到，继续等
                time.sleep(POLL_INTERVAL)
                continue
            except Exception as e:
                log(f"OSS poll error: {e}")
                time.sleep(POLL_INTERVAL)
                continue

        # 超时
        log(f"Request timed out after {REQUEST_TIMEOUT}s: {method}")
        # 清理请求文件
        try:
            self.bucket.delete_object(req_key)
        except Exception:
            pass

        return {
            "jsonrpc": "2.0",
            "id": req_id,
            "error": {
                "code": -32002,
                "message": f"Request timed out after {REQUEST_TIMEOUT}s. Kanzi daemon may not be running."
            }
        }

    def check_daemon_alive(self):
        """检查 Kanzi 电脑上的 daemon 是否在线"""
        try:
            obj = self.bucket.get_object('heartbeat.json')
            data = json.loads(obj.read().decode('utf-8'))
            return data.get('status') == 'running' and data.get('server_alive', False)
        except Exception:
            return False

    def handle_initialize(self, request):
        """处理 MCP initialize 请求"""
        req_id = request.get("id", 1)
        return {
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {
                "protocolVersion": "2024-11-05",
                "capabilities": SERVER_CAPABILITIES,
                "serverInfo": SERVER_INFO
            }
        }

    def handle_initialized(self, request):
        """处理 initialized 通知 — 无需响应"""
        return None

    def handle_tools_list(self, request):
        """处理 tools/list 请求"""
        req_id = request.get("id", 1)
        return {
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {
                "tools": TOOLS
            }
        }

    def handle_tools_call(self, request):
        """处理 tools/call 请求 — 转发到 OSS"""
        req_id = request.get("id", 1)
        params = request.get("params", {})
        tool_name = params.get("name", "")
        arguments = params.get("arguments", {})

        log(f"Tool call: {tool_name}")

        # 检查 daemon 状态
        self.request_count += 1
        if self.request_count % HEARTBEAT_CHECK_INTERVAL == 0:
            if not self.check_daemon_alive():
                log("WARNING: Kanzi daemon may be offline")

        # 转发到 KanziMcpServer
        response = self.send_via_oss(
            method="tools/call",
            params={"name": tool_name, "arguments": arguments},
            req_id=req_id
        )

        if response is None:
            return {
                "jsonrpc": "2.0",
                "id": req_id,
                "error": {
                    "code": -32001,
                    "message": "Failed to communicate with Kanzi via OSS. Check OSS credentials and daemon status."
                }
            }

        return response

    def process_request(self, request):
        """分发 JSON-RPC 请求"""
        method = request.get("method", "")

        if method == "initialize":
            return self.handle_initialize(request)
        elif method == "initialized":
            return self.handle_initialized(request)
        elif method == "tools/list":
            return self.handle_tools_list(request)
        elif method == "tools/call":
            return self.handle_tools_call(request)
        elif method == "ping":
            req_id = request.get("id", 1)
            return {"jsonrpc": "2.0", "id": req_id, "result": {}}
        else:
            req_id = request.get("id", 1)
            log(f"Unknown method: {method}")
            return {
                "jsonrpc": "2.0",
                "id": req_id,
                "error": {
                    "code": -32601,
                    "message": f"Method not found: {method}"
                }
            }

    def run(self):
        """主循环: 从 stdin 读取请求，处理后写回 stdout"""
        log(f"Kanzi MCP Proxy starting (bucket: {BUCKET_NAME})")

        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue

            try:
                request = json.loads(line)
            except json.JSONDecodeError as e:
                log(f"Invalid JSON: {e}")
                continue

            response = self.process_request(request)

            # initialized 通知不需要响应
            if response is None:
                continue

            # 写回 stdout
            response_json = json.dumps(response, ensure_ascii=False)
            sys.stdout.write(response_json + "\n")
            sys.stdout.flush()


if __name__ == '__main__':
    if not ACCESS_KEY_ID or not ACCESS_KEY_SECRET:
        log("ERROR: Set OSS_ACCESS_KEY_ID and OSS_ACCESS_KEY_SECRET env vars")
        sys.exit(1)

    proxy = OSSMcpProxy()
    proxy.run()
