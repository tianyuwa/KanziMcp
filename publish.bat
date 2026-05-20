﻿@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion

set ROOT=%~dp0
set PLUGIN_DIR=%ROOT%src\KanziMcpPlugin
set SERVER_DIR=%ROOT%src\KanziMcpServer
set PLUGIN_INTERFACE_DIR=%ROOT%pluginInterface
set OUTPUT_DIR=%ROOT%Build_MCP

REM ========================================
REM 默认 Kanzi 版本（注意：目录名带 kanzi 前缀）
REM ========================================
if "%1"=="" (
    set KANZI_VERSION=kanzi3.9.10
) else (
    set KANZI_VERSION=kanzi%1
)

set INTERFACE_SRC=%PLUGIN_INTERFACE_DIR%\%KANZI_VERSION%\PluginInterface.dll
set INTERFACE_DST=%ROOT%PluginInterface.dll

echo ========================================
echo  KanziMCP 一键发布脚本
echo ========================================
echo   Kanzi 版本: %KANZI_VERSION%
echo   输出目录:   %OUTPUT_DIR%
echo ========================================
echo.

REM ========================================
REM 参数校验
REM ========================================
if not exist "%INTERFACE_SRC%" (
    echo [ERROR] PluginInterface.dll not found for Kanzi %KANZI_VERSION%
    echo Expected: %INTERFACE_SRC%
    echo Available versions:
    for /d %%i in ("%PLUGIN_INTERFACE_DIR%\*") do echo    %%~nxi
    exit /b 1
)

REM ========================================
REM [1/6] 清理输出目录
REM ========================================
echo [1/6] 清理输出目录...
if exist "%OUTPUT_DIR%" rmdir /S /Q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"

REM ========================================
REM [2/6] 复制对应版本的 PluginInterface.dll
REM ========================================
echo [2/6] 复制 PluginInterface.dll (%KANZI_VERSION%)...
copy /Y "%INTERFACE_SRC%" "%INTERFACE_DST%" >nul

REM ========================================
REM [3/6] 编译插件（--no-restore 避免 NuGet 路径问题）
REM ========================================
echo [3/6] 编译 KanziMcpPlugin...
"C:/Program Files/dotnet/dotnet.exe" build "%PLUGIN_DIR%\KanziMcpPlugin.csproj" -c Release --no-restore
if errorlevel 1 (
    echo [ERROR] 插件编译失败！
    exit /b 1
)

REM ========================================
REM [4/6] 发布 MCP Server（自包含，打包 .NET Runtime）
REM ========================================
echo [4/6] 发布 KanziMcpServer（self-contained）...
"C:/Program Files/dotnet/dotnet.exe" publish "%SERVER_DIR%\KanziMcpServer.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  --no-restore ^
  -o "%OUTPUT_DIR%\KanziMcpServer"
if errorlevel 1 (
    echo [ERROR] Server 发布失败！
    exit /b 1
)

REM ========================================
REM [5/6] 打包 main.exe（test_mcp_client.py → dist\main.exe）
REM ========================================
echo [5/6] 打包 main.exe（PyInstaller）...
"C:\Users\WTY\AppData\Local\Programs\Python\Python312\python.exe" -m PyInstaller main.spec --distpath dist --workpath build 2>&1
if errorlevel 1 (
    echo [ERROR] main.exe 打包失败！
    exit /b 1
)
if not exist "%ROOT%dist\main.exe" (
    echo [ERROR] dist\main.exe 不存在，打包可能失败！
    exit /b 1
)

REM ========================================
REM [6/6] 组装插件 + 辅助文件到输出目录
REM ========================================
echo [6/6] 组装输出目录...

mkdir "%OUTPUT_DIR%\KanziMcpPlugin\%KANZI_VERSION%\"

REM 插件主 DLL
copy /Y "%PLUGIN_DIR%\bin\Release\net48\PluginKanziMCP.dll" "%OUTPUT_DIR%\KanziMcpPlugin\%KANZI_VERSION%\" >nul

REM 插件依赖 DLL
for %%f in (
    "Microsoft.Bcl.AsyncInterfaces.dll"
    "System.Buffers.dll"
    "System.Memory.dll"
    "System.Numerics.Vectors.dll"
    "System.Runtime.CompilerServices.Unsafe.dll"
    "System.Text.Encodings.Web.dll"
    "System.Text.Json.dll"
    "System.Threading.Tasks.Extensions.dll"
    "System.ValueTuple.dll"
) do (
    if exist "%PLUGIN_DIR%\lib\%%~f" (
        copy /Y "%PLUGIN_DIR%\lib\%%~f" "%OUTPUT_DIR%\KanziMcpPlugin\%KANZI_VERSION%\" >nul
    )
)

REM 复制 main.exe
if exist "%ROOT%dist\main.exe" (
    copy /Y "%ROOT%dist\main.exe" "%OUTPUT_DIR%\" >nul
    echo   main.exe 已复制
)

REM 复制 test_mcp_client.py（自动化测试脚本）
if exist "%ROOT%test_mcp_client.py" (
    copy /Y "%ROOT%test_mcp_client.py" "%OUTPUT_DIR%\" >nul
    echo   test_mcp_client.py 已复制
)

REM 复制 README.md
if exist "%ROOT%README.md" (
    copy /Y "%ROOT%README.md" "%OUTPUT_DIR%\" >nul
    echo   README.md 已复制
)

REM ========================================
REM 完成
REM ========================================
echo.
echo ========================================
echo  发布完成！
echo ========================================
echo  输出目录: %OUTPUT_DIR%
echo.
echo  目录结构:
if exist "%OUTPUT_DIR%" (
    dir /B /S "%OUTPUT_DIR%" 2>nul || echo   (无法显示目录结构)
)
echo ========================================

endlocal
