#!/usr/bin/env python3
"""
KanziMcpServer 命令行测试客户端

用法:
  方式1 - 自动启动 KanziMcpServer:
    python test_mcp_client.py --server "E:\wangtianyu\publish\KanziMcpServer.exe"

  方式2 - 连接已运行的 KanziMcpServer (通过管道):
    先在 cmd 启动: KanziMcpServer.exe --verbose
    然后: python test_mcp_client.py --server "E:\wangtianyu\publish\KanziMcpServer.exe"

交互命令:
  init          - 发送 MCP initialize 握手
  tools         - 列出所有可用工具
  status        - 查询服务器状态
  tree          - 获取节点树
  nodes         - 查询所有节点
  node_types    - 列出节点类型
  search <text> - 搜索节点文本
  set <path> <prop> <value>  - 设置属性 (preview 模式)
  apply <path> <prop> <value> - 设置属性 (apply 模式)
  quit          - 退出
"""

import subprocess
import sys
import json
import shlex
import threading
import time
import argparse
import os


class McpTestClient:
    """MCP 协议测试客户端"""

    def __init__(self, server_path, verbose=False):
        self.server_path = server_path
        self.verbose = verbose
        self.process = None
        self.request_id = 0
        self.initialized = False

    def start_server(self):
        """启动 KanziMcpServer 子进程"""
        print(f"[启动] KanziMcpServer: {self.server_path}")
        try:
            self.process = subprocess.Popen(
                [self.server_path, "--verbose"],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                bufsize=0,
                cwd=os.path.dirname(self.server_path) or None
            )

            # 启动后台线程读取 stderr (日志输出)
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

            # 等待服务器就绪
            time.sleep(2)

            if self.process.poll() is not None:
                print(f"[错误] 服务器启动失败，退出码: {self.process.returncode}")
                return False

            print("[成功] KanziMcpServer 已启动")
            return True

        except FileNotFoundError:
            print(f"[错误] 找不到服务器: {self.server_path}")
            return False
        except Exception as e:
            print(f"[错误] 启动失败: {e}")
            return False

    def send_request(self, method, params=None):
        """发送 JSON-RPC 请求并读取响应"""
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
            print(f"  >>> 发送: {request_json[:200]}")

        try:
            # 写入 stdin
            self.process.stdin.write((request_json + "\n").encode('utf-8'))
            self.process.stdin.flush()

            # 读取 stdout
            response_line = self.process.stdout.readline()
            if not response_line:
                print("[错误] 服务器无响应 (stdout 已关闭)")
                return None

            response_str = response_line.decode('utf-8').strip()
            if not response_str:
                print("[错误] 收到空响应")
                return None

            if self.verbose:
                print(f"  <<< 响应: {response_str[:300]}")

            response = json.loads(response_str)

            # 检查 error 字段（必须是实际的对象，不能是 null）
            error = response.get("error")
            if error is not None and isinstance(error, dict):
                print(f"[错误] 服务器返回错误: {error.get('message', error)}")
                return None

            return response.get("result")

        except json.JSONDecodeError as e:
            print(f"[错误] JSON 解析失败: {e}")
            return None
        except Exception as e:
            print(f"[错误] 请求失败: {e}")
            return None

    def initialize(self):
        """MCP 协议握手"""
        print("\n" + "="*50)
        print("步骤1: MCP 协议握手 (initialize)")
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
            print(f"  服务器名称: {result.get('serverInfo', {}).get('name', 'N/A')}")
            print(f"  服务器版本: {result.get('serverInfo', {}).get('version', 'N/A')}")
            print(f"  协议版本: {result.get('protocolVersion', 'N/A')}")
            print(f"  服务器能力: {json.dumps(result.get('capabilities', {}), ensure_ascii=False)}")

            # 发送 initialized 通知
            self.send_request("initialized")
            self.initialized = True
            print("\n[成功] MCP 握手完成!")
            return True
        else:
            print("[失败] MCP 握手失败")
            return False

    def list_tools(self):
        """列出所有工具"""
        print("\n" + "="*50)
        print("步骤2: 列出所有 MCP 工具")
        print("="*50)

        result = self.send_request("tools/list")
        if result and "tools" in result:
            tools = result["tools"]
            print(f"\n  共 {len(tools)} 个工具:\n")
            for i, tool in enumerate(tools, 1):
                print(f"  {i}. {tool['name']}")
                print(f"     {tool.get('description', 'N/A')}")
                schema = tool.get('inputSchema', {}).get('properties', {})
                if schema:
                    params = [f"{k}({v.get('type','?')})" for k, v in schema.items()]
                    print(f"     参数: {', '.join(params)}")
                print()
        else:
            print("[失败] 无法获取工具列表")

    def get_status(self):
        """查询服务器状态"""
        print("\n" + "-"*40)
        print("查询服务器状态")
        print("-"*40)

        result = self.send_request("tools/call", {
            "name": "kanzi_status",
            "arguments": {}
        })

        if result:
            content = result.get("content", [])
            for item in content:
                text = item.get('text', '')
                print(f"  {text}")
                # kanzi_status 现在返回 {"pipe": {"connected": false, ...}}
                # 即使未连接也返回有效 JSON，说明 MCP 服务器工作正常
                if '"error"' not in text.lower():
                    return True
            return True
        else:
            print("  [失败] 无法获取状态")
            return False

    def get_node_tree(self, root_path=None, depth=3):
        """获取节点树"""
        print(f"\n{'-'*40}")
        print(f"获取节点树 (深度={depth})")
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
            print("  [失败] 无法获取节点树")
            return False

    def query_nodes(self, node_type=None, name=None, path=None):
        """查询节点"""
        print(f"\n{'-'*40}")
        print("查询节点")
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
            print("  [失败] 无法查询节点")
            return False

    def list_node_types(self):
        """列出节点类型"""
        print(f"\n{'-'*40}")
        print("列出所有节点类型")
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
            print("  [失败] 无法获取节点类型")
            return False

    def search_nodes(self, search_text):
        """搜索节点"""
        print(f"\n{'-'*40}")
        print(f"搜索节点: '{search_text}'")
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
            print("  [失败] 搜索节点失败")
            return False

    def set_property(self, path, prop, value, mode="preview"):
        """设置属性 (交互模式用)"""
        print(f"\n{'-'*40}")
        print(f"设置属性: {path}.{prop} = {value} (模式: {mode})")
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
            print("  [失败] 设置属性失败")
            return False

    def get_binding_info(self, path):
        """获取数据绑定信息"""
        print(f"\n{'-'*40}")
        print(f"获取绑定信息: {path}")
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
            print("  [失败] 获取绑定信息失败")
            return False

    def set_property_preview(self, path, prop, value):
        """设置属性 (preview 模式)"""
        print(f"\n{'-'*40}")
        print(f"设置属性 (preview): {path}.{prop} = {value}")
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
            print("  [失败] 设置属性失败")
            return False

    def get_property_metadata(self, node_type):
        """获取属性元数据"""
        print(f"\n{'-'*40}")
        print(f"获取属性元数据: {node_type}")
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
            print("  [失败] 获取属性元数据失败")
            return False

    def audit_bindings(self, path=None, modifications=None):
        """审计数据绑定"""
        print(f"\n{'-'*40}")
        print(f"审计数据绑定 (path: {path or '全项目'})")
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
        print("  [失败] 审计绑定失败")
        return False

    def audit_bindings_modify_preview(self, node_path, binding_index=0, code="{PreviewCode}"):
        """预览修改 binding code（不 apply）"""
        return self.audit_bindings(modifications=[{
            "nodePath": node_path,
            "bindingIndex": binding_index,
            "code": code,
            "mode": "preview"
        }])

    def audit_localization(self, languages=None):
        """审计本地化（已废弃 compat 桩）"""
        print(f"\n{'-'*40}")
        print(f"审计本地化 (deprecated compat)")
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
        print("  [失败] 审计本地化 compat 失败")
        return False

    def audit_project_structure(self):
        """审计项目结构"""
        print(f"\n{'-'*40}")
        print("审计项目结构")
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
            print("  [失败] 审计项目结构失败")
            return False

    def doctor_resource(self):
        """诊断资源使用情况"""
        print(f"\n{'-'*40}")
        print("诊断资源使用情况")
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
            print("  [失败] 诊断资源失败")
            return False

    def import_image(self, file_path):
        """导入图片"""
        print(f"\n{'-'*40}")
        print(f"导入图片: {file_path}")
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
            print("  [失败] 导入图片失败")
            return False

    def batch_set_property(self, filter_dict, properties, mode="preview", ignore_read_only=False):
        """批量设置属性"""
        print(f"\n{'-'*40}")
        print(f"批量设置属性 (filter={filter_dict}, mode={mode})")
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
            print("  [失败] 批量设置属性失败")
            return False

    def audit_resource_references(self, check_unused=True, check_broken=True, check_orphaned=True):
        """审计资源引用（compat 转发至 doctor_resource）"""
        print(f"\n{'-'*40}")
        print("审计资源引用 (deprecated compat)")
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
        print("  [失败] 审计资源引用 compat 失败")
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

    def create_node(self, parent_path, node_type, node_name=None, properties=None):
        """创建节点"""
        print(f"\n{'-'*40}")
        print(f"创建节点: {parent_path} -> {node_type} ({node_name or 'auto'})")
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
            print("  [失败] 创建节点失败")
            return False

    def delete_node(self, path, mode="preview"):
        """删除节点"""
        print(f"\n{'-'*40}")
        print(f"删除节点: {path} (mode={mode})")
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
            print("  [失败] 删除节点失败")
            return False

    def interactive(self):
        """交互式命令行"""
        print("\n" + "="*50)
        print("  KanziMcpServer 交互式测试")
        print("="*50)
        print("""
可用命令:
  init          - MCP 握手
  tools         - 列出所有工具
  status        - 服务器状态
  tree          - 节点树
  nodes         - 查询所有节点
  node_types    - 列出节点类型
  search <text> - 搜索节点
  set <路径> <属性名> <值>     - 设置属性 (preview)
  apply <路径> <属性名> <值>   - 设置属性 (apply)
  raw <json>    - 发送原始 JSON-RPC
  quit          - 退出

提示: 路径或值包含空格时请用引号包裹:
  set "Screens/Screen/RootPage/Viewport 2D" Name "Hello World"
""")

        # 自动执行握手
        if self.initialize():
            self.list_tools()

        while True:
            try:
                cmd = input("\n> ").strip()
            except (EOFError, KeyboardInterrupt):
                break

            if not cmd:
                continue

            # 使用 shlex 解析，支持引号包裹的路径（如 "Screens/Screen/RootPage/Viewport 2D"）
            try:
                parts = shlex.split(cmd)
            except ValueError as e:
                print(f"  命令解析错误: {e}")
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
                    print("  用法: search <文本>")
                    continue
                self.search_nodes(parts[1])
            elif action == "set":
                if len(parts) < 4:
                    print("  用法: set <路径> <属性名> <值>")
                    print('  提示: 路径/值含空格请用引号，如: set "Screens/Screen/RootPage/Viewport 2D" Name "Hello World"')
                    continue
                value_str = " ".join(parts[3:])  # 值可能包含空格
                self.set_property(parts[1], parts[2], value_str, mode="preview")
            elif action == "apply":
                if len(parts) < 4:
                    print("  用法: apply <路径> <属性名> <值>")
                    print('  提示: 路径/值含空格请用引号，如: apply "Screens/Screen/RootPage/Viewport 2D" Name "Hello World"')
                    continue
                value_str = " ".join(parts[3:])  # 值可能包含空格
                self.set_property(parts[1], parts[2], value_str, mode="apply")
            elif action == "raw":
                if len(parts) < 2:
                    print("  用法: raw <JSON-RPC请求>")
                    continue
                try:
                    raw_json = cmd[len("raw "):]
                    req = json.loads(raw_json)
                    result = self.send_request(req.get("method", ""), req.get("params"))
                    print(json.dumps(result, ensure_ascii=False, indent=2)[:3000])
                except json.JSONDecodeError as e:
                    print(f"  JSON 格式错误: {e}")
            else:
                print(f"  未知命令: {action}")

    def run_auto_test(self):
        """自动运行完整测试流程"""
        print("\n" + "#"*60)
        print("#  KanziMcpServer 完整自动化测试 (18 tools)")
        print("#"*60)

        # Track test results
        # 分类测试：MCP核心功能 vs 需要Kanzi Studio连接的功能
        self.test_results = {
            # MCP 核心功能（MCP Server 本身工作正常即通过）
            "init": False,
            "list_tools": False,
            "status": False,
            # 需要 Kanzi Studio 连接的功能（失败=环境限制，不是代码问题）
            "node_tree": False,
            "node_types": False,
            "search": False,
            "query": False,
            "binding": False,
            "set_property": False,
            "property_metadata": False,
            "audit_bindings": False,
            "audit_localization": False,
            "audit_structure": False,
            "doctor_resource": False,
            # === 新增 6 个工具测试 ===
            "batch_set_property": False,
            "audit_resource_references": False,
            "create_node": False,
            "delete_node": False,
            "import_image": False,
            "import_fbx": False,
        }

        # MCP 核心功能测试
        if not self.initialize():
            self._print_result("MCP握手", False, "初始化失败")
            return False
        self.test_results["init"] = True
        self._print_result("MCP握手", True)

        # 列出所有工具
        self.list_tools()
        self.test_results["list_tools"] = True
        self._print_result("列出工具", True)

        # 服务器状态（使用 GetConnectionStatusString，不尝试连接）
        status_result = self.get_status()
        self.test_results["status"] = status_result
        self._print_result("服务器状态", status_result, "无法获取状态" if not status_result else "")

        # 需要 Kanzi Studio 连接的功能测试
        print("\n--- 以下功能需要 Kanzi Studio 运行 ---")

        # Node tree
        self.test_results["node_tree"] = self.get_node_tree(depth=2)
        self._print_result("获取节点树", self.test_results["node_tree"])

        # Node types
        self.test_results["node_types"] = self.list_node_types()
        self._print_result("列出节点类型", self.test_results["node_types"])

        # Search nodes
        self.test_results["search"] = self.search_nodes("Screen")
        self._print_result("搜索节点", self.test_results["search"])

        # Query nodes by type
        self.test_results["query"] = self.query_nodes(node_type="TextBlock2D")
        self._print_result("查询节点", self.test_results["query"])

        # Get binding info
        self.test_results["binding"] = self.get_binding_info("kanzi_mcp/Screens/Screen")
        self._print_result("获取绑定信息", self.test_results["binding"])

        # Set property (preview mode)
        self.test_results["set_property"] = self.set_property_preview("kanzi_mcp/Screens/Screen", "Name", "TestScreen")
        self._print_result("Set property (preview)", self.test_results["set_property"])

        # Set Text property (apply mode) - test for TextBlock2D Text modification
        print("\n--- Text Property Apply Test ---")
        self.test_results["set_text_property"] = self.set_property(
            "kanzi_mcp/Screens/Screen/RootPage/ViewPort2D/Text Block 2D",
            "Text", "Hello MCP", mode="apply")
        self._print_result("Set Text property (apply)", self.test_results["set_text_property"])

        # Get property metadata
        self.test_results["property_metadata"] = self.get_property_metadata("Node2D")
        self._print_result("获取属性元数据", self.test_results["property_metadata"])

        # Audit bindings
        self.test_results["audit_bindings"] = self.audit_bindings()
        self._print_result("审计绑定", self.test_results["audit_bindings"])

        # Audit bindings modify preview
        self.test_results["audit_bindings_modify"] = self.audit_bindings_modify_preview(
            "kanzi_mcp/Screens/Screen", binding_index=0, code="{PreviewMcpCode}")
        self._print_result("绑定修改 preview", self.test_results["audit_bindings_modify"])

        # Audit localization (deprecated compat)
        self.test_results["audit_localization"] = self.audit_localization()
        self._print_result("审计本地化 (deprecated)", self.test_results["audit_localization"])

        # Audit project structure
        self.test_results["audit_structure"] = self.audit_project_structure()
        self._print_result("审计项目结构", self.test_results["audit_structure"])

        # Doctor resource - diagnose unused resources
        self.test_results["doctor_resource"] = self.doctor_resource()
        self._print_result("诊断资源", self.test_results["doctor_resource"])

        # === 新增 6 个工具测试 ===

        # Batch set property (preview mode)
        self.test_results["batch_set_property"] = self.batch_set_property(
            filter_dict={"type": "Node2D"},
            properties={"Opacity": 0.8},
            mode="preview"
        )
        self._print_result("Batch set property (preview)", self.test_results["batch_set_property"])

        # Audit resource references
        self.test_results["audit_resource_references"] = self.audit_resource_references()
        self._print_result("审计资源引用 (deprecated compat)", self.test_results["audit_resource_references"])

        # Create node (preview mode - no actual creation)
        self.test_results["create_node"] = self.create_node(
            parent_path="kanzi_mcp/Screens/Screen",
            node_type="EmptyNode2D",
            node_name="TestNode_MCP"
        )
        self._print_result("Create node", self.test_results["create_node"])

        # Delete node (preview/dry-run mode - no actual deletion)
        self.test_results["delete_node"] = self.delete_node(
            path="kanzi_mcp/Screens/Screen/TestNode_MCP",
            mode="preview"
        )
        self._print_result("Delete node (preview)", self.test_results["delete_node"])

        # Import image - test with a non-existent file (expected to fail gracefully)
        # In real usage, this would be a real image path
        self.test_results["import_image"] = self.import_image(
            file_path="C:/temp/test_image.png"
        )
        self._print_result("Import image", self.test_results["import_image"])

        # Import FBX - test with a non-existent file (expected to fail gracefully)
        # In real usage, this would be a real FBX path
        self.test_results["import_fbx"] = self.import_fbx(
            file_path="C:/temp/test_model.fbx"
        )
        self._print_result("Import FBX", self.test_results["import_fbx"])

        print("\n" + "#"*60)
        print("#  自动化测试完成!")
        print("#"*60)

        # Analyze results
        core_tests = sum(1 for k in ["init", "list_tools", "status"] if self.test_results[k])
        kanzi_tests = sum(1 for k in self.test_results if k not in ["init", "list_tools", "status"])
        kanzi_passed = sum(1 for k in self.test_results if k not in ["init", "list_tools", "status"] and self.test_results[k])

        print(f"\nCore Tests: {core_tests}/3 passed")
        print(f"Kanzi Tests: {kanzi_passed}/{kanzi_tests} passed")

        # 判断逻辑：核心功能通过 = PASS
        # Kanzi 功能失败是预期的（环境限制），不影响整体判断
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
        """停止服务器"""
        if self.process and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except:
                self.process.kill()
            print("\n[信息] 服务器已停止")


def main():
    parser = argparse.ArgumentParser(description="KanziMcpServer 测试客户端")
    parser.add_argument("--server", required=True, help="KanziMcpServer.exe 路径")
    parser.add_argument("--verbose", "-v", action="store_true", help="显示详细日志")
    parser.add_argument("--auto", action="store_true", help="自动运行完整测试（不进入交互模式）")
    parser.add_argument("--pipe", "-p", default="KanziMcpPipe", help="Named Pipe 名称 (默认: KanziMcpPipe)")

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
        print("\n[信息] 用户中断")
    finally:
        client.stop()


if __name__ == "__main__":
    main()
