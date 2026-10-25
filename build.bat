@echo off
chcp 65001 >nul
title 发票汇总工具 - 一键本地编译

echo ==========================================
echo   发票汇总工具 v1.3 - 本地编译脚本
echo ==========================================
echo.

:: 检查 dotnet 是否存在
where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    echo [错误] 未检测到 .NET SDK，请先安装：https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

:: 显示 SDK 版本
echo [1/3] 检查环境...
for /f "tokens=*" %%i in ('dotnet --version') do set SDK_VER=%%i
echo      .NET SDK 版本：%SDK_VER%
echo.

:: 清理旧缓存
echo [2/3] 清理旧缓存...
dotnet clean -c Release >nul
if exist "publish" rmdir /s /q "publish"
echo      完成
echo.

:: 编译单文件
echo [3/3] 正在编译单文件 exe（约需 30-60 秒）...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

if %errorlevel% neq 0 (
    echo.
    echo [失败] 编译出错，请检查上面的错误信息。
    pause
    exit /b 1
)

echo.
echo ==========================================
echo   ✅ 编译成功！
echo ==========================================
echo.
echo   输出目录：%~dp0publish
echo   可执行文件：InvoiceTool.exe
echo.

:: 自动打开文件夹
explorer "%~dp0publish"

:: 询问是否立即运行
echo 是否立即运行程序？(Y/N)
set /p RUN_NOW=
if /i "%RUN_NOW%"=="Y" (
    start "" "%~dp0publish\InvoiceTool.exe"
)

echo.
echo 窗口将在 3 秒后自动关闭...
timeout /t 3 >nul