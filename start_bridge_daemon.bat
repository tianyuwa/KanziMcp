@echo off
:: 启动 OSS Bridge Daemon
:: 用法: 双击运行，或命令行 python oss_bridge_daemon.py

echo ============================================================
echo  OSS Bridge Daemon for KanziMcpServer
echo ============================================================
echo.

:: 检查环境变量
if "%OSS_ACCESS_KEY_ID%"=="" (
    echo [ERROR] OSS_ACCESS_KEY_ID not set
    goto :eof
)
if "%OSS_ACCESS_KEY_SECRET%"=="" (
    echo [ERROR] OSS_ACCESS_KEY_SECRET not set
    goto :eof
)

:: 检查 Python
python --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Python not found. Please install Python 3.x
    goto :eof
)

:: 检查 oss2
python -c "import oss2" >nul 2>&1
if errorlevel 1 (
    echo [INFO] Installing oss2...
    pip install oss2
)

:: 检查 KanziMcpServer.exe
if not exist "C:\KanziMonitor\Build_MCP\KanziMcpServer\KanziMcpServer.exe" (
    echo [WARNING] KanziMcpServer.exe not found at default path
    echo          Set KANZI_SERVER_PATH env var if different
)

echo Starting daemon...
echo Press Ctrl+C to stop
echo.

python "%~dp0oss_bridge_daemon.py"

pause
