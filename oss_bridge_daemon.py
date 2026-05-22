#!/usr/bin/env python3
"""
OSS Bridge Daemon v2 - Kanzi Computer (Packaged as EXE)
Enhanced stability: auto-restart, watchdog, log file, exponential backoff

Polls OSS (claudemcp bucket) for requests -> forwards to KanziMcpServer.exe -> uploads results back

Environment Variables:
  OSS_ACCESS_KEY_ID     - Aliyun AccessKey ID
  OSS_ACCESS_KEY_SECRET - Aliyun AccessKey Secret
  OSS_ENDPOINT          - OSS endpoint (default: oss-cn-beijing.aliyuncs.com)
  OSS_BUCKET            - OSS bucket name (default: claudemcp)
  KANZI_SERVER_PATH     - KanziMcpServer.exe path
  POLL_INTERVAL         - Poll interval in seconds (default: 0.5)
  REQUEST_TIMEOUT       - Request processing timeout in seconds (default: 30)
  LOG_FILE              - Log file path (default: oss_bridge_daemon.log next to exe)
"""

import oss2
import subprocess
import json
import sys
import os
import time
import threading
import queue
import traceback

# ============ Config ============
ENDPOINT = os.environ.get('OSS_ENDPOINT', 'oss-cn-beijing.aliyuncs.com')
ACCESS_KEY_ID = os.environ.get('OSS_ACCESS_KEY_ID', '')
ACCESS_KEY_SECRET = os.environ.get('OSS_ACCESS_KEY_SECRET', '')
BUCKET_NAME = os.environ.get('OSS_BUCKET', 'claudemcp')

KANZI_SERVER_PATH = os.environ.get(
    'KANZI_SERVER_PATH',
    r'C:\KanziMonitor\Build_MCP\KanziMcpServer\KanziMcpServer.exe'
)
POLL_INTERVAL = float(os.environ.get('POLL_INTERVAL', '0.5'))
REQUEST_TIMEOUT = int(os.environ.get('REQUEST_TIMEOUT', '30'))

REQUEST_PREFIX = 'requests/'
RESPONSE_PREFIX = 'responses/'

# Auto-restart settings
MAX_RESTART_ATTEMPTS = 10
RESTART_BASE_DELAY = 3  # seconds, will double each attempt
HEARTBEAT_EVERY_N_POLLS = 20  # upload heartbeat every N polls

# Track processed request IDs (with TTL cleanup)
processed_requests = {}
PROCESSED_TTL = 600  # 10 minutes


def get_log_file():
    """Log file next to the exe"""
    if getattr(sys, 'frozen', False):
        exe_dir = os.path.dirname(sys.executable)
    else:
        exe_dir = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(exe_dir, 'oss_bridge_daemon.log')


LOG_FILE = os.environ.get('LOG_FILE', get_log_file())


def log(msg):
    """Print + write to log file"""
    line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}"
    print(line, flush=True)
    try:
        with open(LOG_FILE, 'a', encoding='utf-8') as f:
            f.write(line + '\n')
    except Exception:
        pass


def cleanup_processed_requests():
    """Remove old entries from processed_requests to prevent memory growth"""
    now = time.time()
    expired = [k for k, t in processed_requests.items() if now - t > PROCESSED_TTL]
    for k in expired:
        del processed_requests[k]
    if expired:
        log(f"Cleaned {len(expired)} expired request IDs from cache")


class KanziServerBridge:
    """Manages KanziMcpServer subprocess, forwards JSON-RPC requests"""

    def __init__(self, server_path):
        self.server_path = server_path
        self.process = None
        self.request_id = 0
        self.initialized = False
        self._lock = threading.Lock()
        self._response_queue = queue.Queue()
        self._stdout_alive = False
        self._stderr_thread = None
        self._stdout_thread = None

    def start(self):
        log(f"Starting KanziMcpServer: {self.server_path}")

        # Clean up old process if any
        self.stop()

        if not os.path.exists(self.server_path):
            log(f"ERROR: Server not found: {self.server_path}")
            return False

        try:
            self.process = subprocess.Popen(
                [self.server_path, '--verbose'],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                bufsize=0,
                cwd=os.path.dirname(self.server_path) or None
            )

            self._stdout_alive = True

            # Background thread to read stderr logs
            def read_stderr():
                try:
                    while True:
                        line = self.process.stderr.readline()
                        if not line:
                            break
                        try:
                            msg = line.decode('utf-8', errors='replace').strip()
                            if msg:
                                log(f"  [Server] {msg}")
                        except Exception:
                            pass
                except Exception as e:
                    log(f"  [Server] stderr thread error: {e}")

            self._stderr_thread = threading.Thread(target=read_stderr, daemon=True)
            self._stderr_thread.start()

            # Background thread to read stdout responses
            def read_stdout():
                try:
                    buffer = b''
                    while True:
                        char = self.process.stdout.read(1)
                        if not char:
                            break
                        buffer += char
                        if char == b'\n':
                            line = buffer.decode('utf-8', errors='replace').strip()
                            buffer = b''
                            if line:
                                self._response_queue.put(line)
                except Exception as e:
                    log(f"  [Server] stdout thread error: {e}")
                finally:
                    self._stdout_alive = False

            self._stdout_thread = threading.Thread(target=read_stdout, daemon=True)
            self._stdout_thread.start()

            # Wait for server to be ready
            time.sleep(2)

            if self.process.poll() is not None:
                log(f"ERROR: Server exited with code {self.process.returncode}")
                self._stdout_alive = False
                return False

            log("KanziMcpServer started successfully")
            return True

        except Exception as e:
            log(f"ERROR: Failed to start server: {e}")
            self._stdout_alive = False
            return False

    def initialize(self):
        log("Performing MCP initialize handshake...")
        result = self.send_request("initialize", {
            "protocolVersion": "2024-11-05",
            "clientInfo": {
                "name": "oss-bridge-daemon",
                "version": "2.0.0"
            },
            "capabilities": {}
        })

        if result:
            server_name = result.get('serverInfo', {}).get('name', 'N/A')
            log(f"Connected to: {server_name}")
            self.send_request("initialized", None)
            self.initialized = True
            log("MCP handshake complete")
            return True
        else:
            log("ERROR: MCP handshake failed")
            return False

    def send_request(self, method, params=None):
        if not self.is_alive():
            log("  Cannot send request: server not alive")
            return None

        with self._lock:
            self.request_id += 1
            req_id = self.request_id

        request = {
            "jsonrpc": "2.0",
            "id": req_id,
            "method": method
        }
        if params is not None:
            request["params"] = params

        request_json = json.dumps(request, ensure_ascii=False)

        try:
            self.process.stdin.write((request_json + "\n").encode('utf-8'))
            self.process.stdin.flush()

            response_line = self._read_response(timeout=REQUEST_TIMEOUT)
            if not response_line:
                return None

            response = json.loads(response_line)

            error = response.get("error")
            if error is not None and isinstance(error, dict):
                log(f"  Server error: {error.get('message', error)}")
                return None

            return response.get("result")

        except BrokenPipeError:
            log("  Broken pipe: server process likely died")
            return None
        except Exception as e:
            log(f"  Request failed: {e}")
            return None

    def _read_response(self, timeout=30):
        try:
            return self._response_queue.get(timeout=timeout)
        except queue.Empty:
            log("  Response timeout")
            return None

    def forward_request(self, rpc_request):
        method = rpc_request.get('method', '')
        params = rpc_request.get('params')

        log(f"  Forwarding: {method}")
        result = self.send_request(method, params)

        response = {
            "jsonrpc": "2.0",
            "id": rpc_request.get("id", 1),
        }

        if result is not None:
            response["result"] = result
        else:
            response["error"] = {
                "code": -32000,
                "message": "Forward to KanziMcpServer failed"
            }

        return response

    def is_alive(self):
        return self.process is not None and self.process.poll() is None

    def is_healthy(self):
        """Check both process and stdout reader thread"""
        return self.is_alive() and self._stdout_alive

    def stop(self):
        if self.process:
            if self.process.poll() is None:
                try:
                    self.process.terminate()
                    try:
                        self.process.wait(timeout=5)
                    except Exception:
                        self.process.kill()
                except Exception:
                    pass
                log("KanziMcpServer stopped")
            self.process = None
        self._stdout_alive = False
        self.initialized = False
        # Clear stale responses from queue
        try:
            while not self._response_queue.empty():
                self._response_queue.get_nowait()
        except Exception:
            pass


class OSSBridge:
    """OSS request/response bridge with auto-restart"""

    def __init__(self):
        auth = oss2.Auth(ACCESS_KEY_ID, ACCESS_KEY_SECRET)
        self.bucket = oss2.Bucket(auth, ENDPOINT, BUCKET_NAME)
        self.server = None
        self._poll_count = 0

    def _connect_server(self):
        """Start or restart KanziMcpServer with retry"""
        self.server = KanziServerBridge(KANZI_SERVER_PATH)
        return self.server.start() and self.server.initialize()

    def run(self):
        """Main entry: runs forever with auto-restart on crash"""
        log("=" * 60)
        log("OSS Bridge Daemon v2 Starting")
        log(f"  Bucket: {BUCKET_NAME}")
        log(f"  Endpoint: {ENDPOINT}")
        log(f"  Server: {KANZI_SERVER_PATH}")
        log(f"  Log: {LOG_FILE}")
        log("=" * 60)

        restart_count = 0

        while True:
            try:
                # Try to connect to server
                if not self._connect_server():
                    restart_count += 1
                    delay = min(RESTART_BASE_DELAY * (2 ** (restart_count - 1)), 60)
                    log(f"Connection failed. Retry {restart_count}/{MAX_RESTART_ATTEMPTS} in {delay}s...")
                    self._upload_heartbeat(status="reconnecting")
                    time.sleep(delay)
                    if restart_count >= MAX_RESTART_ATTEMPTS:
                        log(f"FATAL: {MAX_RESTART_ATTEMPTS} connection attempts failed. Waiting 60s before reset.")
                        self._upload_heartbeat(status="error")
                        time.sleep(60)
                        restart_count = 0
                    continue

                # Connected successfully, reset counter
                restart_count = 0
                self._upload_heartbeat(status="running")
                log("Entering poll loop...")

                # Inner poll loop
                while True:
                    # Health check: if server or stdout reader died, break to reconnect
                    if not self.server.is_healthy():
                        reason = "not alive" if not self.server.is_alive() else "stdout reader died"
                        log(f"WARNING: Server {reason}, reconnecting...")
                        self.server.stop()
                        break

                    self._poll_requests()

                    # Periodic heartbeat + cleanup
                    self._poll_count += 1
                    if self._poll_count % HEARTBEAT_EVERY_N_POLLS == 0:
                        self._upload_heartbeat(status="running")
                    if self._poll_count % (HEARTBEAT_EVERY_N_POLLS * 3) == 0:
                        cleanup_processed_requests()

                    time.sleep(POLL_INTERVAL)

            except KeyboardInterrupt:
                log("Interrupted by user")
                self._upload_heartbeat(status="stopped")
                break
            except Exception as e:
                log(f"UNEXPECTED ERROR: {e}")
                log(traceback.format_exc())
                self._upload_heartbeat(status="error")
                restart_count += 1
                delay = min(RESTART_BASE_DELAY * (2 ** (restart_count - 1)), 60)
                log(f"Restarting in {delay}s (attempt {restart_count})...")
                time.sleep(delay)
                if restart_count >= MAX_RESTART_ATTEMPTS:
                    log("Too many restarts, waiting 60s before reset")
                    time.sleep(60)
                    restart_count = 0
            finally:
                if self.server:
                    self.server.stop()

        log("Daemon shutdown complete")

    def _poll_requests(self):
        try:
            objects = self.bucket.list_objects(prefix=REQUEST_PREFIX, max_keys=100)

            if not objects.object_list:
                return

            for obj in objects.object_list:
                key = obj.key
                if key == REQUEST_PREFIX or key.endswith('/'):
                    continue

                filename = os.path.basename(key)
                req_id = filename.replace('mcp_req_', '').replace('.json', '')

                if req_id in processed_requests:
                    continue

                log(f"New request: {filename}")
                processed_requests[req_id] = time.time()

                try:
                    req_data = self.bucket.get_object(key).read()
                    rpc_request = json.loads(req_data.decode('utf-8'))

                    response = self.server.forward_request(rpc_request)

                    resp_key = f"{RESPONSE_PREFIX}mcp_resp_{req_id}.json"
                    resp_data = json.dumps(response, ensure_ascii=False).encode('utf-8')
                    self.bucket.put_object(resp_key, resp_data)
                    log(f"  Response uploaded: mcp_resp_{req_id}.json")

                    self.bucket.delete_object(key)
                    log(f"  Request deleted: {filename}")

                except Exception as e:
                    log(f"  Error processing {filename}: {e}")

                    error_resp = {
                        "jsonrpc": "2.0",
                        "id": req_id,
                        "error": {
                            "code": -32001,
                            "message": f"Bridge processing error: {e}"
                        }
                    }
                    resp_key = f"{RESPONSE_PREFIX}mcp_resp_{req_id}.json"
                    self.bucket.put_object(resp_key, json.dumps(error_resp).encode('utf-8'))
                    self.bucket.delete_object(key)

        except Exception as e:
            log(f"Poll error: {e}")

    def _upload_heartbeat(self, status="running"):
        try:
            heartbeat = json.dumps({
                "daemon": "oss_bridge_daemon",
                "version": "2.0.0",
                "status": status,
                "server_alive": self.server.is_healthy() if self.server else False,
                "timestamp": time.strftime('%Y-%m-%d %H:%M:%S'),
                "processed_cache_size": len(processed_requests)
            }, ensure_ascii=False).encode('utf-8')
            self.bucket.put_object('heartbeat.json', heartbeat)
        except Exception as e:
            log(f"Heartbeat upload failed: {e}")


def main():
    if not ACCESS_KEY_ID or not ACCESS_KEY_SECRET:
        print("ERROR: Please set OSS_ACCESS_KEY_ID and OSS_ACCESS_KEY_SECRET environment variables")
        print("")
        print("Run in PowerShell:")
        print('  [System.Environment]::SetEnvironmentVariable("OSS_ACCESS_KEY_ID", "your_key_id", "User")')
        print('  [System.Environment]::SetEnvironmentVariable("OSS_ACCESS_KEY_SECRET", "your_key_secret", "User")')
        input("Press Enter to exit...")
        sys.exit(1)

    bridge = OSSBridge()
    bridge.run()


if __name__ == '__main__':
    main()
