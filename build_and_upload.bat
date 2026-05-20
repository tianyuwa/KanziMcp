@echo off
echo [WorkBuddy] Starting build and upload...

:: 1. 编译并打包
call publish.bat
if %errorlevel% neq 0 (
    echo Build failed!
    exit /b %errorlevel%
)

:: 2. 检查 Build_MCP 目录是否存在
set OUTPUT_DIR=C:\Users\WTY\WorkBuddy\kanziMcpServer\Build_MCP
if not exist "%OUTPUT_DIR%\" (
    echo Build_MCP directory not found at %OUTPUT_DIR%
    exit /b 1
)

:: 3. 上传整个 Build_MCP 目录到 OSS（递归上传，覆盖写入）
echo Uploading Build_MCP to OSS...
"C:\Users\WTY\WorkBuddy\ossutil-v1.7.19-windows-amd64\ossutil64.exe" cp --recursive "%OUTPUT_DIR%" oss://mcpkanzipublish/incoming/Build_MCP/ --force
if %errorlevel% neq 0 (
    echo Upload failed!
    exit /b %errorlevel%
)

:: 4. 生成并上传 latest_build.txt（用于监控脚本检测新构建）
echo Build completed at %date% %time% > "%OUTPUT_DIR%\latest_build.txt"
"C:\Users\WTY\WorkBuddy\ossutil-v1.7.19-windows-amd64\ossutil64.exe" cp "%OUTPUT_DIR%\latest_build.txt" oss://mcpkanzipublish/incoming/latest_build.txt --force
del "%OUTPUT_DIR%\latest_build.txt"
if %errorlevel% neq 0 (
    echo Failed to upload latest_build.txt!
    exit /b %errorlevel%
)

echo Build and upload completed successfully.
exit /b 0
