@echo off
title InvoiceTool Build Script

:: 强制 dotnet 工具输出英文日志
set DOTNET_CLI_UI_LANGUAGE=en-US

echo ==========================================
echo   InvoiceTool v1.3 - Local Build Script
echo ==========================================
echo.

where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] .NET SDK not found.
    pause
    exit /b 1
)

echo [1/3] Checking environment...
for /f "tokens=*" %%i in ('dotnet --version') do set SDK_VER=%%i
echo       .NET SDK: %SDK_VER%
echo.

echo [2/3] Cleaning old cache...
dotnet clean -c Release >nul
if exist "publish" rmdir /s /q "publish"
echo       Done.
echo.

echo [3/3] Compiling single-file exe...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

if %errorlevel% neq 0 (
    echo.
    echo [FAILED] Compilation error.
    pause
    exit /b 1
)

echo.
echo ==========================================
echo   BUILD SUCCESSFUL
echo ==========================================
echo.
echo   Output: %~dp0publish\InvoiceTool.exe
echo.

explorer "%~dp0publish"

echo Run now? (Y/N)
set /p RUN_NOW=
if /i "%RUN_NOW%"=="Y" (
    start "" "%~dp0publish\InvoiceTool.exe"
)

echo.
echo Closing in 3 seconds...
timeout /t 3 >nul