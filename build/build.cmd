@echo off
setlocal
chcp 65001 >nul

rem ============================================================
rem  Server Monitor - one-click build
rem
rem  Run this on the machine that has the .NET SDK. The output goes to
rem    build\Release\net45\
rem  Copy that one exe to the target server (no runtime install needed).
rem
rem  That path is not set here: it comes from the project itself
rem  (OutputPath in ServerMonitor.csproj, BaseIntermediateOutputPath in
rem  Directory.Build.props), so a plain "dotnet build" lands in exactly the
rem  same place as this script. Nothing is written under src\ any more.
rem
rem  NOTE: the app keeps its runtime data (servers.json with the encrypted
rem  passwords, settings.json, history, disk-daily.json) in a "data" folder
rem  NEXT TO THE EXE - that is, inside build\. The clean step below deletes
rem  build\Release\, so data\ is moved out to TEMP first and moved back when
rem  the build ends, including when it fails. If the data cannot be moved
rem  out, the build stops before deleting anything.
rem
rem  Keep this file ASCII-only: cmd re-reads a .cmd file by byte offset after
rem  chcp changes the codepage, and multi-byte characters shift those offsets,
rem  which makes cmd execute half a comment line. English comments only.
rem
rem  Every abort goes through a :fail_ label at the bottom. Do not replace
rem  those with an inline "exit /b 1": cmd loses the errorlevel of an exit
rem  inside an if/else block, so the build would report success on failure
rem  (measured: the caller saw exit code 0 for a failed build).
rem ============================================================

cd /d "%~dp0.."

set "DATADIR=build\Release\net45\data"
set "BACKUP=%TEMP%\ServerMonitor-build-data"

rem An empty variable would turn the tests below into "\*" (the drive root),
rem so the script would think the data was moved out and delete bin anyway.
if not exist "src\ServerMonitor\ServerMonitor.csproj" goto :fail_root
if "%DATADIR%"=="" goto :fail_datadir

echo [1/4] Protecting runtime data (servers, settings, history)...
if exist "%BACKUP%" (
    if exist "%DATADIR%\*" (
        rem A backup from a crashed run is still there and the app has written
        rem data since: keep both, use a different name, never overwrite one.
        set "BACKUP=%TEMP%\ServerMonitor-build-data-%RANDOM%%RANDOM%"
        echo   NOTE: an older backup is still at %TEMP%\ServerMonitor-build-data
    ) else (
        rem robocopy, not move: cmd's built-in move cannot move a directory
        rem across drives (D: to C: here) - it fails with "Access is denied".
        robocopy "%BACKUP%" "%DATADIR%" /e /move /nfl /ndl /njh /njs /np /r:1 /w:1 >nul
        if errorlevel 8 (
            rem Leave it where it is: the copy below merges bin\ into the same
            rem folder, and step [4/4] puts the whole thing back afterwards.
            echo   WARNING: the old backup could not be put back into build\.
            echo            It stays at %BACKUP% and is restored after the build.
        ) else (
            echo   Recovered the backup left by an interrupted build.
        )
    )
)
if exist "%DATADIR%\*" (
    robocopy "%DATADIR%" "%BACKUP%" /e /move /nfl /ndl /njh /njs /np /r:1 /w:1 >nul
    if errorlevel 8 (
        rem robocopy /move goes file by file, so a failure part-way leaves the
        rem earlier files in BACKUP and the rest in the output folder - the
        rem data would be split in two. Put those back first so the program
        rem still runs, and stop before anything is deleted.
        robocopy "%BACKUP%" "%DATADIR%" /e /move /nfl /ndl /njh /njs /np /r:1 /w:1 >nul
        if errorlevel 8 goto :fail_copyout_split
        goto :fail_copyout
    )
    echo   Moved %DATADIR%
    echo     to  %BACKUP%
) else (
    echo   No runtime data to protect.
)

echo [2/4] Cleaning previous build output...
rem Only the Release output: build\ itself holds this script, make-icon.ps1
rem and the icon preview, so never clean the whole folder.
if exist "build\Release" rd /s /q "build\Release"
if exist "build\obj" rd /s /q "build\obj"

echo [3/4] Building Release...
dotnet build "src\ServerMonitor\ServerMonitor.csproj" -c Release -v minimal --nologo
if errorlevel 1 set "BUILD_FAILED=1"

echo [4/4] Putting runtime data back...
if exist "%BACKUP%\*" (
    if not exist "build\Release\net45" mkdir "build\Release\net45"
    robocopy "%BACKUP%" "%DATADIR%" /e /move /nfl /ndl /njh /njs /np /r:1 /w:1 >nul
    if errorlevel 8 goto :fail_restore
    echo   Restored %DATADIR%
) else (
    echo   Nothing to restore.
)

if defined BUILD_FAILED goto :fail_build

echo Done. Output directory:
echo   %CD%\build\Release\net45\
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
exit /b 0

rem ---------------------------------------------------------------
rem  Failure exits. Kept out of the blocks above on purpose (see the
rem  note in the header): only a top-level exit /b keeps its code.
rem ---------------------------------------------------------------

:fail_root
echo ERROR: %CD% does not look like the repository root - nothing was done.
exit /b 1

:fail_datadir
echo ERROR: DATADIR is empty - nothing was done.
exit /b 1

:fail_copyout
echo.
echo ERROR: cannot copy %DATADIR% out of the output folder - nothing was deleted.
echo        Close the program if it is running and build again.
exit /b 1

:fail_copyout_split
echo.
echo ERROR: cannot copy %DATADIR% out of the output folder - nothing was deleted.
echo        Part of it had already been moved out and could not be put back:
echo          %BACKUP%
echo        Move that folder back into %DATADIR% by hand.
exit /b 1

:fail_restore
echo.
echo ERROR: could not copy the runtime data back. It is still at
echo        %BACKUP%
echo        Move it to %DATADIR% by hand before running the program.
exit /b 1

:fail_build
echo.
echo Build failed. See the errors above.
exit /b 1
