#!/usr/bin/env python3
"""
KanziMcpServer Test Client

Usage:
  Mode 1 - Auto-start KanziMcpServer:
    python test_mcp_client.py --server "E:\wangtianyu\publish\KanziMcpServer.exe"

  Mode 2 - Connect to running KanziMcpServer (via pipe):
    First start in cmd: KanziMcpServer.exe --verbose
    Then: python test_mcp_client.py --server "E:\wangtianyu\publish\KanziMcpServer.exe"

Interactive commands:
  init          - Send MCP initialize handshake
  tools         - List all available tools
  status        - Check server status
  tree          - Get node tree
  nodes         - Query all nodes
  node_types    - List node types
  search <text> - Search node text
  set <path> <prop> <value>  - Set property (preview mode)
  apply <path> <prop> <value> - Set property (apply mode)
  quit          - Quit
"""

import subprocess
import sys
import json
import shlex
import threading
import time
import argparse
import os


# Kanzi Monitor auto-test project paths (Untitled / kanziMCP3910)
TEST_PATHS = {
    "screen": "Screens/Screen",
    "viewport": "Screens/Screen/RootPage/Viewport 2D",
    "text_2d": "Screens/Screen/RootPage/Viewport 2D/Text Block 2D",
    "text_2d_1": "Screens/Screen/RootPage/Viewport 2D/Text Block 2D_1",
    "scene": "Screens/Screen/RootPage/Viewport 2D/Scene",
    "test_text_2d": "Screens/Screen/RootPage/Viewport 2D/Test_Text_2D",
}
IMPORT_IMAGE_PATH = os.environ.get(
    "KANZI_TEST_IMAGE",
    "E:/wangtianyu/localization_Test/Image/L3_NCA_STANDBY.png",
)
EXPECTED_TOOL_COUNT = 18


class McpTestClient:
    """MCP Protocol Test Client"""

    def __init__(self, server_path, verbose=False):
        self.server_path = server_path
        self.verbose = verbose
        self.auto_mode = False
        self.process = None
        self.request_id = 0
        self.initialized = False

    def start_server(self):
        """Start KanziMcpServer subprocess"""
        print(f"[Start] KanziMcpServer: {self.server_path}")
        try:
            # Auto mode (non-interactive): skip --verbose to avoid stderr pipe deadlock
            # (PowerShell CreateNoWindow=true has limited stderr pipe, verbose output would block)
            server_args = [self.server_path]
            if self.verbose:
                server_args.append("--verbose")

            self.process = subprocess.Popen(
                server_args,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                bufsize=0,
                cwd=os.path.dirname(self.server_path) or None
            )

            # Background thread to read stderr (log output)
            def read_stderr():
                while True:
                    line = self.process.stderr.readline()
                    if not line:
                        break
                    try:
                        msg = line.decode('utf-8', errors='replace').strip()
                        if msg:
                            print(f"  [Server] {msg}")
                    except:
                        pass

            t = threading.Thread(target=read_stderr, daemon=True)
            t.start()

            # Wait for server to be ready
            time.sleep(2)

            if self.process.poll() is not None:
                print(f"[ERROR] Server start failed, exit code: {self.process.returncode}")
                return False

            print("[OK] KanziMcpServer started")
            return True

        except FileNotFoundError:
            print(f"[ERROR] Server not found: {self.server_path}")
            return False
        except Exception as e:
            print(f"[ERROR] Start failed: {e}")
            return False

    def send_request(self, method, params=None):
        """Send JSON-RPC request and read response"""
        self.request_id += 1
        request = {
            "jsonrpc": "2.0",
            "id": self.request_id,
            "method": method
        }
        if params is not None:
            request["params"] = params

        request_json = json.dumps(request, ensure_ascii=False)

        if self.verbose:
            print(f"  >>> >>> Send: {request_json[:200]}")

        try:
            # Write to stdin
            self.process.stdin.write((request_json + "\n").encode('utf-8'))
            self.process.stdin.flush()

            # Read stdout
            response_line = self.process.stdout.readline()
            if not response_line:
                print("[ERROR] Server not responding (stdout closed)")
                return None

            response_str = response_line.decode('utf-8').strip()
            if not response_str:
                print("[ERROR] Received empty response")
                return None

            if self.verbose:
                print(f"  <<< <<< Response: {response_str[:300]}")

            response = json.loads(response_str)

            # Check error field (must be dict object, not null)
            error = response.get("error")
            if error is not None and isinstance(error, dict):
                print(f"[ERROR] Server returned error: {error.get('message', error)}")
                return None

            return response.get("result")

        except json.JSONDecodeError as e:
            print(f"[ERROR] JSON parse failed: {e}")
            return None
        except Exception as e:
            print(f"[ERROR] Request failed: {e}")
            return None

    def initialize(self):
        """MCP protocol handshake"""
        print("\n" + "="*50)
        print("Step1: MCP protocol handshake (initialize)")
        print("="*50)

        result = self.send_request("initialize", {
            "protocolVersion": "2024-11-05",
            "clientInfo": {
                "name": "test-client",
                "version": "1.0.0"
            },
            "capabilities": {}
        })

        if result:
            print(f"  Server name: {result.get('serverInfo', {}).get('name', 'N/A')}")
            print(f"  Server version: {result.get('serverInfo', {}).get('version', 'N/A')}")
            print(f"  Protocol version: {result.get('protocolVersion', 'N/A')}")
            print(f"  Server capabilities: {json.dumps(result.get('capabilities', {}), ensure_ascii=False)}")

            # Send initialized notification
            self.send_request("initialized")
            self.initialized = True
            print("\n[OK] MCP handshake complete!")
            return True
        else:
            print("[FAIL] MCP handshake failed")
            return False

    def list_tools(self, expected_min=EXPECTED_TOOL_COUNT):
        """List all tools"""
        print("\n" + "="*50)
        print("Step 2: List all MCP tools")
        print("="*50)

        result = self.send_request("tools/list")
        if result and "tools" in result:
            tools = result["tools"]
            print(f"\n  Total {len(tools)} tools (expected >= {expected_min}):\n")
            for i, tool in enumerate(tools, 1):
                print(f"  {i}. {tool['name']}")
                if not self.auto_mode:
                    print(f"     {tool.get('description', 'N/A')}")
                    schema = tool.get('inputSchema', {}).get('properties', {})
                    if schema:
                        params = [f"{k}({v.get('type','?')})" for k, v in schema.items()]
                        print(f"     Args: {', '.join(params)}")
                print()
            return len(tools) >= expected_min
        else:
            print("[FAIL] Failed to get tool list")
            return False

    def get_status(self):
        """Check server status"""
        print("\n" + "-"*40)
        print("Check server status")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_get_status",
            "arguments": {}
        })

        parsed = self._extract_json_result(result)
        if parsed is not None:
            self._print_parsed(parsed)
            return parsed.get("success", True) is not False
        print("  [FAIL] Failed to get status")
        return False

    def get_node_tree(self, root_path=None, depth=3):
        """Get node tree"""
        print(f"\n{'-'*40}")
        print(f"Get node tree (depth={depth})")
        print("-"*40)

        args = {"depth": depth}
        if root_path:
            args["rootPath"] = root_path

        result = self.send_request("tools/call", {
            "name": "kanzi_get_node_tree",
            "arguments": args
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Failed to get node tree")
            return False

    def query_nodes(self, node_type=None, name=None, path=None):
        """Query nodes"""
        print(f"\n{'-'*40}")
        print("Query nodes")
        print("-"*40)

        args = {"recursive": True, "limit": 50}
        if node_type:
            args["type"] = node_type
        if name:
            args["name"] = name
        if path:
            args["path"] = path

        result = self.send_request("tools/call", {
            "name": "kanzi_query_nodes",
            "arguments": args
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Failed to query nodes")
            return False

    def list_node_types(self):
        """List node types"""
        print(f"\n{'-'*40}")
        print("List all node types")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_list_node_types",
            "arguments": {}
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Failed to get node types")
            return False

    def search_nodes(self, search_text):
        """Search nodes"""
        print(f"\n{'-'*40}")
        print(f"Search nodes: '{search_text}'")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_search_nodes",
            "arguments": {
                "searchText": search_text
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Search nodes failed")
            return False

    def set_property(self, path, prop, value, mode="preview"):
        """Set property (interactive mode)"""
        print(f"\n{'-'*40}")
        print(f"Set property: {path}.{prop} = {value} (mode: {mode})")
        print("-"*40)

        parsed_value = value
        try:
            parsed_value = json.loads(value)
        except:
            pass

        result = self.send_request("tools/call", {
            "name": "kanzi_set_node_property",
            "arguments": {
                "path": path,
                "property": prop,
                "value": parsed_value,
                "mode": mode
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Set property failed")
            return False

    def get_binding_info(self, path):
        """Get data binding info"""
        print(f"\n{'-'*40}")
        print(f"Get binding info: {path}")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_get_binding_info",
            "arguments": {
                "path": path,
                "includeMetadata": True
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Get binding info failed")
            return False

    def set_property_preview(self, path, prop, value):
        """Set property (preview mode)"""
        print(f"\n{'-'*40}")
        print(f"Set property (preview): {path}.{prop} = {value}")
        print("-"*40)

        parsed_value = value
        try:
            parsed_value = json.loads(value)
        except:
            pass

        result = self.send_request("tools/call", {
            "name": "kanzi_set_node_property",
            "arguments": {
                "path": path,
                "property": prop,
                "value": parsed_value,
                "mode": "preview"
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Set property failed")
            return False

    def get_property_metadata(self, node_type):
        """Get property metadata"""
        print(f"\n{'-'*40}")
        print(f"Get property metadata: {node_type}")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_get_property_metadata",
            "arguments": {
                "nodeType": node_type
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Get property metadata failed")
            return False

    def audit_bindings(self, path=None, modifications=None):
        """Audit data bindings"""
        print(f"\n{'-'*40}")
        print(f"Audit data bindings (path: {path or 'entire project'})")
        print("-"*40)

        args = {
            "checkPriority": True,
            "findOrphans": True
        }
        if path:
            args["path"] = path
        if modifications:
            args["modifications"] = modifications

        result = self.send_request("tools/call", {
            "name": "kanzi_audit_bindings",
            "arguments": args
        })

        parsed = self._extract_json_result(result)
        if parsed is not None:
            print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
            return parsed.get("success", True) is not False
        print("  [FAIL] Audit bindings failed")
        return False

    def audit_bindings_modify_preview(self, node_path, binding_index=0, code="{PreviewCode}"):
        """Preview binding code modification (no apply)"""
        return self.audit_bindings(modifications=[{
            "nodePath": node_path,
            "bindingIndex": binding_index,
            "code": code,
            "mode": "preview"
        }])

    def audit_localization(self, languages=None):
        """Audit localization (deprecated compat stub)"""
        print(f"\n{'-'*40}")
        print("Audit localization (deprecated compat)")
        print("-"*40)

        args = {
            "languages": languages or [],
            "includeUntranslated": True
        }

        result = self.send_request("tools/call", {
            "name": "kanzi_audit_localization",
            "arguments": args
        })

        parsed = self._extract_json_result(result)
        if parsed is not None:
            print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
            return parsed.get("deprecated") is True
        print("  [FAIL] Audit localization compat failed")
        return False

    def audit_project_structure(self):
        """Audit project structure"""
        print(f"\n{'-'*40}")
        print("Audit project structure")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_audit_project_structure",
            "arguments": {
                "namingPattern": "^[A-Z][a-zA-Z0-9]*$",
                "checkDepth": True,
                "checkNaming": True
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Audit project structure failed")
            return False

    def doctor_resource(self):
        """Diagnose resource usage"""
        print(f"\n{'-'*40}")
        print("Diagnose resource usage")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_doctor_resource",
            "arguments": {
                "checkImages": True,
                "checkTextures": True
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Diagnose resource failed")
            return False

    def import_image(self, file_path):
        """Import image"""
        print(f"\n{'-'*40}")
        print(f"Import image: {file_path}")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_import_image",
            "arguments": {
                "filePath": file_path,
                "targetFolder": "Textures"
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Import image failed")
            return False

    def import_fbx(self, file_path):
        """Import FBX 3D model"""
        print(f"\n{'-'*40}")
        print(f"Import FBX: {file_path}")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_import_fbx",
            "arguments": {
                "filePath": file_path,
                "targetFolder": "Meshes"
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Import FBX failed")
            return False

    def batch_set_property(self, filter_dict, properties, mode="preview", ignore_read_only=False):
        """Batch set property"""
        print(f"\n{'-'*40}")
        print(f"Batch set property (filter={filter_dict}, mode={mode})")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_batch_set_property",
            "arguments": {
                "filter": filter_dict,
                "properties": properties,
                "mode": mode,
                "ignoreReadOnly": ignore_read_only
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Batch set property failed")
            return False

    def audit_resource_references(self, check_unused=True, check_broken=True, check_orphaned=True):
        """Audit resource references (compat redirect to doctor_resource)"""
        print(f"\n{'-'*40}")
        print("Audit resource references (deprecated compat)")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_audit_resource_references",
            "arguments": {
                "checkUnused": check_unused,
                "checkBroken": check_broken,
                "checkOrphaned": check_orphaned
            }
        })

        parsed = self._extract_json_result(result)
        if parsed is not None:
            print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
            return (
                parsed.get("deprecated") is True
                and parsed.get("redirectTo") == "kanzi_doctor_resource"
                and parsed.get("success") is True
            )
        print("  [FAIL] Audit resource references compat failed")
        return False

    def _extract_json_result(self, result):
        if not result:
            return None
        content = result.get("content", [])
        for item in content:
            text = item.get("text", "")
            try:
                return json.loads(text)
            except Exception:
                print(text[:3000])
        return None

    def _print_parsed(self, parsed, limit=3000):
        if self.auto_mode:
            summary = {
                k: parsed[k]
                for k in ("success", "count", "error", "message", "totalBatches",
                          "batchStatesCreated", "affectedCount", "deprecated", "redirectTo")
                if k in parsed
            }
            if summary:
                print(f"  {json.dumps(summary, ensure_ascii=False)}")
            else:
                print(f"  keys: {list(parsed.keys())[:12]}")
        else:
            print(json.dumps(parsed, ensure_ascii=False, indent=2)[:limit])

    def _tool_call(self, name, arguments, label=None, require_success=True):
        if label:
            print(f"\n{'-'*40}")
            print(label)
            print("-"*40)

        result = self.send_request("tools/call", {
            "name": name,
            "arguments": arguments
        })
        parsed = self._extract_json_result(result)
        if parsed is None:
            print("  [FAIL] No JSON response")
            return False

        self._print_parsed(parsed)
        if not require_success:
            return True
        if parsed.get("success") is True:
            return True
        if parsed.get("deprecated") is True:
            return True
        if parsed.get("success") is False:
            return False
        if parsed.get("error"):
            return False
        # Tools that return data without explicit success (e.g. status payloads)
        return True

    def upsert_custom_enum_property(self, name, options, mode="preview",
                                    display_name=None, category=None):
        """Create or update custom enum property"""
        args = {"name": name, "options": options, "mode": mode}
        if display_name:
            args["displayName"] = display_name
        if category:
            args["category"] = category
        return self._tool_call(
            "kanzi_upsert_custom_enum_property",
            args,
            label=f"Upsert custom enum: {name} (mode={mode})",
        )

    def create_state_manager(self, manager_name, group_name, group_property, states,
                             bind_node_path="", mode="preview", auto_generate_count=0,
                             batch_size=12, batch_index=0, confirm_large_batch=False):
        """Create state manager (preview or batched apply)"""
        args = {
            "managerName": manager_name,
            "groupName": group_name,
            "groupProperty": group_property,
            "states": states,
            "mode": mode,
            "batchSize": batch_size,
            "batchIndex": batch_index,
        }
        if bind_node_path:
            args["bindNodePath"] = bind_node_path
        if auto_generate_count:
            args["autoGenerateCount"] = auto_generate_count
        if confirm_large_batch:
            args["confirmLargeBatch"] = True
        return self._tool_call(
            "kanzi_create_state_manager",
            args,
            label=f"Create state manager: {manager_name}/{group_name} (mode={mode})",
        )

    def create_node(self, parent_path, node_type, node_name=None, properties=None):
        """Create node"""
        print(f"\n{'-'*40}")
        print(f"Create node: {parent_path} -> {node_type} ({node_name or 'auto'})")
        print("-"*40)

        args = {
            "parentPath": parent_path,
            "nodeType": node_type
        }
        if node_name:
            args["nodeName"] = node_name
        if properties:
            args["properties"] = properties

        result = self.send_request("tools/call", {
            "name": "kanzi_create_node",
            "arguments": args
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Create node failed")
            return False

    def delete_node(self, path, mode="preview"):
        """Delete node"""
        print(f"\n{'-'*40}")
        print(f"Delete node: {path} (mode={mode})")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_delete_node",
            "arguments": {
                "path": path,
                "mode": mode
            }
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get("text", "")
                try:
                    parsed = json.loads(text)
                    print(json.dumps(parsed, ensure_ascii=False, indent=2)[:3000])
                except:
                    print(text[:3000])
            return True
        else:
            print("  [FAIL] Delete node failed")
            return False

    def interactive(self):
        """Interactive command line"""
        print("\n" + "="*50)
        print("  KanziMcpServer Interactive Test")
        print("="*50)
        print("""
Available commands:
  init          - MCP handshake
  tools         - List all tools
  status        - Server status
  tree          - Node tree
  nodes         - Query all nodes
  node_types    - List node types
  search <text> - Search nodes
  set <path> <property> <value>     - Set property (preview)
  apply <path> <property> <value>   - Set property (apply)
  raw <json>    - Send raw JSON-RPC
  quit          - Quit

Tip: Use quotes for paths/values with spaces:
  set "Screens/Screen/RootPage/Viewport 2D" Name "Hello World"
""")

        # Auto-execute handshake
        if self.initialize():
            self.list_tools()

        while True:
            try:
                cmd = input("\n> ").strip()
            except (EOFError, KeyboardInterrupt):
                break

            if not cmd:
                continue

            # Use shlex to parse, supports quoted paths (e.g. "Screens/Screen/RootPage/Viewport 2D")
            try:
                parts = shlex.split(cmd)
            except ValueError as e:
                print(f"  Command parse error: {e}")
                continue

            action = parts[0].lower()

            if action == "quit" or action == "exit":
                break
            elif action == "init":
                self.initialize()
            elif action == "tools":
                self.list_tools()
            elif action == "status":
                self.get_status()
            elif action == "tree":
                root = parts[1] if len(parts) > 1 else None
                self.get_node_tree(root_path=root)
            elif action == "nodes":
                ntype = parts[1] if len(parts) > 1 else None
                self.query_nodes(node_type=ntype)
            elif action == "node_types":
                self.list_node_types()
            elif action == "search":
                if len(parts) < 2:
                    print("  Usage: search <text>")
                    continue
                self.search_nodes(parts[1])
            elif action == "set":
                if len(parts) < 4:
                    print("  Usage: set <path> <property> <value>")
                    print('  Tip: Use quotes for paths/values with spaces, e.g.: set "Screens/Screen/RootPage/Viewport 2D" Name "Hello World"')
                    continue
                value_str = " ".join(parts[3:])  # value may contain spaces
                self.set_property(parts[1], parts[2], value_str, mode="preview")
            elif action == "apply":
                if len(parts) < 4:
                    print("  Usage: apply <path> <property> <value>")
                    print('  Tip: Use quotes for paths/values with spaces, e.g.: apply "Screens/Screen/RootPage/Viewport 2D" Name "Hello World"')
                    continue
                value_str = " ".join(parts[3:])  # value may contain spaces
                self.set_property(parts[1], parts[2], value_str, mode="apply")
            elif action == "raw":
                if len(parts) < 2:
                    print("  Usage: raw <JSON-RPC request>")
                    continue
                try:
                    raw_json = cmd[len("raw "):]
                    req = json.loads(raw_json)
                    result = self.send_request(req.get("method", ""), req.get("params"))
                    print(json.dumps(result, ensure_ascii=False, indent=2)[:3000])
                except json.JSONDecodeError as e:
                    print(f"  JSON format error: {e}")
            else:
                print(f"  Unknown command: {action}")

    def run_auto_test(self):
        """Auto-run complete test flow (Kanzi Monitor: C:\\KanziMonitor\\Build_MCP\\main.exe --auto)"""
        self.auto_mode = True
        p = TEST_PATHS

        print("\n" + "#"*60)
        print("#  KanziMcpServer Auto Test (18 MCP tools + compat)")
        print("#"*60)

        self.test_results = {
            # MCP core (PASS requires 3/3)
            "init": False,
            "list_tools": False,
            "status": False,
            # Kanzi Studio (informational)
            "node_tree": False,
            "node_types": False,
            "search": False,
            "query_by_type": False,
            "query_by_path": False,
            "binding": False,
            "property_metadata": False,
            "set_property_preview": False,
            "set_text_preview": False,
            "batch_font_preview": False,
            "audit_bindings": False,
            "audit_structure": False,
            "doctor_resource": False,
            "batch_set_property": False,
            "create_node": False,
            "delete_node_preview": False,
            "import_image": False,
            "upsert_enum_preview": False,
            "state_manager_preview": False,
            # Deprecated compat (optional)
            "audit_localization": False,
            "audit_resource_references": False,
        }

        if not self.initialize():
            self._print_result("MCP Handshake", False, "Init failed")
            return False
        self.test_results["init"] = True
        self._print_result("MCP Handshake", True)

        self.test_results["list_tools"] = self.list_tools()
        self._print_result("List tools", self.test_results["list_tools"])

        self.test_results["status"] = self.get_status()
        self._print_result("Server status", self.test_results["status"])

        print("\n--- Kanzi Studio tests (require Studio + open project) ---")

        self.test_results["node_tree"] = self._tool_call(
            "kanzi_get_node_tree",
            {"rootPath": p["screen"], "depth": 3, "includeProperties": False},
            label=f"Get node tree: {p['screen']} depth=3",
        )
        self._print_result("Get node tree", self.test_results["node_tree"])

        self.test_results["node_types"] = self._tool_call(
            "kanzi_list_node_types", {},
            label="List node types",
        )
        self._print_result("List node types", self.test_results["node_types"])

        self.test_results["search"] = self._tool_call(
            "kanzi_search_nodes",
            {
                "searchText": "Text Block 2D",
                "searchIn": ["Name", "Path", "Type"],
                "caseSensitive": True,
            },
            label="Search nodes: Text Block 2D (case sensitive)",
        )
        self._print_result("Search nodes", self.test_results["search"])

        self.test_results["query_by_type"] = self._tool_call(
            "kanzi_query_nodes",
            {"type": "Text Block 2D", "limit": 50, "recursive": True},
            label="Query nodes by type: Text Block 2D",
        )
        self._print_result("Query by type", self.test_results["query_by_type"])

        self.test_results["query_by_path"] = self._tool_call(
            "kanzi_query_nodes",
            {
                "path": p["text_2d_1"],
                "includeProperties": True,
                "includeBindings": True,
                "recursive": False,
                "limit": 1,
            },
            label=f"Query node detail: {p['text_2d_1']}",
        )
        self._print_result("Query by path", self.test_results["query_by_path"])

        self.test_results["binding"] = self._tool_call(
            "kanzi_get_binding_info",
            {"path": p["text_2d_1"], "includeMetadata": True},
            label=f"Get binding info: {p['text_2d_1']}",
        )
        self._print_result("Get binding info", self.test_results["binding"])

        self.test_results["property_metadata"] = self._tool_call(
            "kanzi_get_property_metadata",
            {"nodeType": "Text Block 2D"},
            label="Property metadata: Text Block 2D",
        )
        self._print_result("Property metadata", self.test_results["property_metadata"])

        self.test_results["set_property_preview"] = self._tool_call(
            "kanzi_set_node_property",
            {"path": p["screen"], "property": "Name", "value": "TestScreen", "mode": "preview"},
            label=f"Set property preview: {p['screen']}.Name",
        )
        self._print_result("Set property (preview)", self.test_results["set_property_preview"])

        self.test_results["set_text_preview"] = self._tool_call(
            "kanzi_set_node_property",
            {
                "path": p["text_2d"],
                "property": "TextConcept.Text",
                "value": "KanziMCP AutoTest",
                "mode": "preview",
            },
            label=f"Set text preview: {p['text_2d']}",
        )
        self._print_result("Set text (preview)", self.test_results["set_text_preview"])

        self.test_results["batch_font_preview"] = self._tool_call(
            "kanzi_batch_set_property",
            {
                "filter": {"type": "Text Block 2D", "recursive": True},
                "properties": {"FontStyleConcept.Size": 150},
                "mode": "preview",
            },
            label="Batch font size preview (FontStyleConcept.Size=150)",
        )
        self._print_result("Batch font size (preview)", self.test_results["batch_font_preview"])

        self.test_results["audit_bindings"] = self.audit_bindings()
        self._print_result("Audit bindings", self.test_results["audit_bindings"])

        self.test_results["audit_structure"] = self._tool_call(
            "kanzi_audit_project_structure",
            {
                "checkDepth": True,
                "checkNaming": True,
                "namingPattern": "^[a-z][a-zA-Z0-9]*$",
            },
            label="Audit project structure",
        )
        self._print_result("Audit project structure", self.test_results["audit_structure"])

        self.test_results["doctor_resource"] = self._tool_call(
            "kanzi_doctor_resource",
            {"checkImages": True, "checkTextures": True, "checkBroken": False},
            label="Doctor resource",
        )
        self._print_result("Doctor resource", self.test_results["doctor_resource"])

        self.test_results["batch_set_property"] = self._tool_call(
            "kanzi_batch_set_property",
            {
                "filter": {"type": "Text Block 2D", "recursive": True},
                "properties": {"TextConcept.Text": "all test"},
                "mode": "preview",
            },
            label="Batch set text preview",
        )
        self._print_result("Batch set property (preview)", self.test_results["batch_set_property"])

        self.test_results["create_node"] = self._tool_call(
            "kanzi_create_node",
            {
                "parentPath": p["scene"],
                "nodeType": "Text Block 3D",
                "nodeName": "MCP_AutoTest_3D",
            },
            label=f"Create node: Text Block 3D under {p['scene']}",
        )
        self._print_result("Create node", self.test_results["create_node"])

        self.test_results["delete_node_preview"] = self._tool_call(
            "kanzi_delete_node",
            {"path": p["test_text_2d"], "mode": "preview"},
            label=f"Delete node preview: {p['test_text_2d']}",
        )
        self._print_result("Delete node (preview)", self.test_results["delete_node_preview"])

        if os.path.exists(IMPORT_IMAGE_PATH):
            self.test_results["import_image"] = self._tool_call(
                "kanzi_import_image",
                {"filePath": IMPORT_IMAGE_PATH, "targetFolder": "Textures"},
                label=f"Import image: {IMPORT_IMAGE_PATH}",
            )
        else:
            print(f"\n  [SKIP] Import image - file not found: {IMPORT_IMAGE_PATH}")
            self.test_results["import_image"] = True
        self._print_result("Import image", self.test_results["import_image"])

        self.test_results["upsert_enum_preview"] = self.upsert_custom_enum_property(
            name="MCP_AutoTest_Enum",
            options=[
                {"name": "Test1", "value": 1},
                {"name": "Test2", "value": 2},
            ],
            mode="preview",
            display_name="MCP AutoTest Enum",
        )
        self._print_result("Upsert enum (preview)", self.test_results["upsert_enum_preview"])

        self.test_results["state_manager_preview"] = self.create_state_manager(
            manager_name="MCP_AutoTest_Manager",
            group_name="MCP_AutoTest_Group",
            group_property="warnvalue",
            bind_node_path=p["viewport"],
            mode="preview",
            auto_generate_count=3,
            batch_size=12,
            states=[{
                "stateName": "warn_{0}",
                "statePropertyValue": 1,
                "objects": [{
                    "nodeName": "Text Block 2D",
                    "nodePath": p["text_2d"],
                    "properties": {"TextConcept.Text": "warning_{0}"},
                }],
            }],
        )
        self._print_result("State manager (preview)", self.test_results["state_manager_preview"])

        print("\n--- Deprecated compat tools ---")
        self.test_results["audit_localization"] = self.audit_localization()
        self._print_result("Audit localization (deprecated)", self.test_results["audit_localization"])

        self.test_results["audit_resource_references"] = self.audit_resource_references()
        self._print_result("Audit resource references (deprecated)", self.test_results["audit_resource_references"])

        print("\n" + "#"*60)
        print("#  Automated test complete!")
        print("#"*60)

        core_keys = ["init", "list_tools", "status"]
        kanzi_keys = [k for k in self.test_results if k not in core_keys]
        core_tests = sum(1 for k in core_keys if self.test_results[k])
        kanzi_passed = sum(1 for k in kanzi_keys if self.test_results[k])
        kanzi_tests = len(kanzi_keys)

        print(f"\nCore Tests: {core_tests}/3 passed")
        print(f"Kanzi Tests: {kanzi_passed}/{kanzi_tests} passed")

        if core_tests >= 3:
            print("\nTEST_RESULT: PASS")
            print("Note: MCP Server core functionality is working.")
            if kanzi_passed < kanzi_tests:
                print(f"      Kanzi tests: {kanzi_passed}/{kanzi_tests} passed (requires Kanzi Studio).")
            return True
        else:
            print("\nTEST_RESULT: FAIL")
            print("Note: MCP Server core functionality is broken.")
            return False

    def _print_result(self, name, passed, error_msg=""):
        """Print test result for a single test"""
        if passed:
            print(f"  [PASS] {name}")
        else:
            print(f"  [FAIL] {name}" + (f": {error_msg}" if error_msg else ""))

    def stop(self):
        """Stop server"""
        if self.process and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except:
                self.process.kill()
            print("\n[INFO] Server stopped")


def main():
    parser = argparse.ArgumentParser(description="KanziMcpServer Test Client")
    parser.add_argument("--server", required=True, help="Path to KanziMcpServer.exe")
    parser.add_argument("--verbose", "-v", action="store_true", help="Show verbose log")
    parser.add_argument("--auto", action="store_true", help="Auto-run full test (non-interactive)")
    parser.add_argument("--pipe", "-p", default="KanziMcpPipe", help="Named Pipe name (default: KanziMcpPipe)")

    args = parser.parse_args()

    client = McpTestClient(args.server, verbose=args.verbose)

    if not client.start_server():
        sys.exit(1)

    try:
        if args.auto:
            client.run_auto_test()
        else:
            client.interactive()
    except KeyboardInterrupt:
        print("\n[INFO] User interrupted")
    finally:
        client.stop()


if __name__ == "__main__":
    main()
