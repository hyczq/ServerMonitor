@echo off
setlocal
chcp 65001 >nul

rem ============================================================
rem  Server Monitor - one-click build
rem
rem  Run this on the machine that has the .NET SDK. The output goes to
rem    src\ServerMonitor\bin\Release\net45\
rem  Copy that one exe to the target server (no runtime install needed).
rem ============================================================

cd /d "%~dp0.."

echo [1/3] Cleaning previous build output...
if exist "src\ServerMonitor\bin" rd /s /q "src\ServerMonitor\bin"
if exist "src\ServerMonitor\obj" rd /s /q "src\ServerMonitor\obj"

echo [2/3] Building Release...
dotnet build "src\ServerMonitor\ServerMonitor.csproj" -c Release -v minimal --nologo
if errorlevel 1 (
    echo.
    echo Build failed. See the errors above.
    exit /b 1
)

echo [3/3] Done. Output directory:
echo   %CD%\src\ServerMonitor\bin\Release\net45\
echo.
echo Copy this file to the target server - it is the only one needed:
echo   ServerMonitor.exe
echo.
echo Newtonsoft.Json and Renci.SshNet are merged into the exe at build time.
echo ServerMonitor.pdb is only needed to debug crashes, never for deployment.
echo.
echo The target server only needs .NET Framework 4.5 or later
echo (Windows Server 2012 R2 already ships 4.5.1).
echo.

endlocal
