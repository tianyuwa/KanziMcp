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
  REQUEST_TIMEOUT       - 等待响应超时秒数（默认 30）
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
REQUEST_TIMEOUT = int(os.environ.get('REQUEST_TIMEOUT', '30'))

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
    {
        "name": "kanzi_get_status",
        "description": "Get Kanzi MCP server status and connection info",
        "inputSchema": {
            "type": "object",
            "properties": {}
        }
    },
    {
        "name": "kanzi_get_node_tree",
        "description": "Get the Kanzi project node tree. Optionally filter by root path and depth.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "rootPath": {"type": "string", "description": "Starting node path (empty for root)"},
                "depth": {"type": "integer", "description": "Max depth (default: 3)"}
            }
        }
    },
    {
        "name": "kanzi_list_nodes",
        "description": "List all nodes in the project, optionally filtered by type",
        "inputSchema": {
            "type": "object",
            "properties": {
                "type": {"type": "string", "description": "Node type filter (e.g. TextBlock2D, Node2D)"},
                "recursive": {"type": "boolean", "description": "Search recursively (default: true)"}
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
        "name": "kanzi_search_nodes",
        "description": "Search nodes by text content in their properties",
        "inputSchema": {
            "type": "object",
            "properties": {
                "text": {"type": "string", "description": "Text to search for"},
                "maxResults": {"type": "integer", "description": "Max results (default: 20)"}
            }
        }
    },
    {
        "name": "kanzi_get_node_properties",
        "description": "Get all properties of a specific node",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Node path (e.g. Screens/Screen/RootPage/Viewport2D)"},
                "includeMetadata": {"type": "boolean", "description": "Include property metadata"}
            }
        }
    },
    {
        "name": "kanzi_set_node_property",
        "description": "Set a property value on a node. Use mode 'preview' to test without applying.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Node path"},
                "property": {"type": "string", "description": "Property name (e.g. TextConcept.Text, LayoutHorizontalAlignment)"},
                "value": {"type": "string", "description": "Property value"},
                "mode": {"type": "string", "enum": ["preview", "apply"], "description": "preview=test only, apply=commit to project"}
            }
        }
    },
    {
        "name": "kanzi_get_binding_info",
        "description": "Get data binding information for a node",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Node path"},
                "includeMetadata": {"type": "boolean", "description": "Include binding metadata"}
            }
        }
    },
    {
        "name": "kanzi_get_property_metadata",
        "description": "Get property metadata for a specific node type",
        "inputSchema": {
            "type": "object",
            "properties": {
                "nodeType": {"type": "string", "description": "Node type (e.g. TextBlock2D)"}
            }
        }
    },
    {
        "name": "kanzi_create_node",
        "description": "Create a new node under a parent node",
        "inputSchema": {
            "type": "object",
            "properties": {
                "parentPath": {"type": "string", "description": "Parent node path"},
                "nodeType": {"type": "string", "description": "Node type (e.g. Empty Node 2D, Text Block 2D)"},
                "nodeName": {"type": "string", "description": "Name for the new node"},
                "properties": {"type": "object", "description": "Initial properties"}
            }
        }
    },
    {
        "name": "kanzi_delete_node",
        "description": "Delete a node from the project",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Node path to delete"},
                "mode": {"type": "string", "enum": ["preview", "apply"], "description": "preview=dry run, apply=actually delete"}
            }
        }
    },
    {
        "name": "kanzi_batch_set_property",
        "description": "Batch set properties on multiple nodes matching a filter",
        "inputSchema": {
            "type": "object",
            "properties": {
                "filter": {"type": "object", "description": "Filter: {type, pathPrefix, namePattern}"},
                "properties": {"type": "object", "description": "Properties to set"},
                "mode": {"type": "string", "enum": ["preview", "apply"]},
                "ignoreReadOnly": {"type": "boolean"}
            }
        }
    },
    {
        "name": "kanzi_audit_bindings",
        "description": "Audit data bindings across the project",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Scope path (empty for full project)"},
                "checkPriority": {"type": "boolean"},
                "findOrphans": {"type": "boolean"}
            }
        }
    },
    {
        "name": "kanzi_audit_localization",
        "description": "Audit localization coverage",
        "inputSchema": {
            "type": "object",
            "properties": {
                "languages": {"type": "array", "items": {"type": "string"}},
                "includeUntranslated": {"type": "boolean"}
            }
        }
    },
    {
        "name": "kanzi_audit_project_structure",
        "description": "Audit project structure for naming and depth issues",
        "inputSchema": {
            "type": "object",
            "properties": {
                "namingPattern": {"type": "string"},
                "checkDepth": {"type": "boolean"},
                "checkNaming": {"type": "boolean"}
            }
        }
    },
    {
        "name": "kanzi_doctor_resource",
        "description": "Diagnose resource usage (unused images, textures, etc.)",
        "inputSchema": {
            "type": "object",
            "properties": {
                "checkImages": {"type": "boolean"},
                "checkTextures": {"type": "boolean"}
            }
        }
    },
    {
        "name": "kanzi_audit_resource_references",
        "description": "Audit resource references for broken/unused/orphaned resources",
        "inputSchema": {
            "type": "object",
            "properties": {
                "checkUnused": {"type": "boolean"},
                "checkBroken": {"type": "boolean"},
                "checkOrphaned": {"type": "boolean"}
            }
        }
    },
    {
        "name": "kanzi_import_image",
        "description": "Import an image file into the Kanzi project",
        "inputSchema": {
            "type": "object",
            "properties": {
                "filePath": {"type": "string", "description": "Local file path of the image"},
                "targetFolder": {"type": "string", "description": "Target folder in project (e.g. Textures)"}
            }
        }
    },
    {
        "name": "kanzi_import_fbx",
        "description": "Import an FBX 3D model file into the Kanzi project",
        "inputSchema": {
            "type": "object",
            "properties": {
                "filePath": {"type": "string", "description": "Local file path of the FBX model"},
                "targetFolder": {"type": "string", "description": "Target folder in project (e.g. Models)"}
            }
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
