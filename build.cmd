@echo off
setlocal
chcp 65001 >nul

rem ============================================================
rem  服务器监控台 - 一键构建
rem
rem  在装有 .NET SDK 的机器上运行本脚本，产物在
rem    src\ServerMonitor\bin\Release\net45\
rem  把该目录整个拷贝到目标服务器即可运行（无需安装任何运行时）。
rem ============================================================

cd /d "%~dp0"

echo [1/3] 清理旧的构建输出...
if exist "src\ServerMonitor\bin" rd /s /q "src\ServerMonitor\bin"
if exist "src\ServerMonitor\obj" rd /s /q "src\ServerMonitor\obj"

echo [2/3] 编译 Release 版本...
dotnet build "src\ServerMonitor\ServerMonitor.csproj" -c Release -v minimal --nologo
if errorlevel 1 (
    echo.
    echo 构建失败，请检查上面的错误信息。
    exit /b 1
)

echo [3/3] 完成。产物目录：
echo   %~dp0src\ServerMonitor\bin\Release\net45\
echo.
echo 需要拷贝到目标服务器的文件：
echo   ServerMonitor.exe
echo   ServerMonitor.exe.config
echo   Newtonsoft.Json.dll
echo   Renci.SshNet.dll
echo.
echo 目标服务器只需安装 .NET Framework 4.5 或更高版本
echo （Windows Server 2012 R2 已自带 4.5.1，无需额外安装）。
echo.

endlocal
