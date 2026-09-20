@echo off
rem NOTE: NOT using "enabledelayedexpansion" - it would mangle passwords
rem containing the "!" character.
setlocal

rem ==================================================================
rem  Configure a Windows server as an SSH monitoring target
rem  for ServerMonitor.  *** RECOMMENDED PATH ***
rem
rem  Why SSH beats WMI:
rem    * Commands run LOCALLY on the target, so none of the remote
rem      DCOM / UAC-token-filtering / RPC-port problems apply.
rem    * Only one port (22) needs to be open.
rem    * The account does NOT need to be a local administrator
rem      (Performance Monitor Users is enough for CPU counters).
rem
rem  Does three things:
rem    1. creates the monitoring account
rem    2. installs OpenSSH Server from .\OpenSSH-Win64
rem    3. opens the firewall and starts the service
rem
rem  Usage: right-click -> "Run as administrator"
rem  Needs the whole tools folder (including OpenSSH-Win64) on the server.
rem
rem  Output is ASCII-only ON PURPOSE: UTF-8 batch files are mis-parsed
rem  by cmd.exe because multi-byte trailing bytes collide with & | etc.
rem ==================================================================

rem ---- defaults ----
rem ACCOUNT can be changed here; PASSWD is deliberately left empty so the
rem script asks for it. A hard-coded default password in a public repo
rem means everyone who runs this gets the same known credential.
rem Use the quoted "set VAR=value" form: without the quotes a password
rem containing & | < > ^ would be split by cmd and silently stored WRONG.
set "ACCOUNT=monitor"
set "PASSWD="

set "SRC=%~dp0OpenSSH-Win64"
set "DST=%ProgramFiles%\OpenSSH"

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [ERROR] Administrator privileges required.
    echo         Right-click this file and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo.
echo ==================================================================
echo   Configure SSH monitoring target
echo   Host: %COMPUTERNAME%     %DATE% %TIME%
echo ==================================================================

rem ask for the password (not echoed to the screen)
if "%PASSWD%"=="" (
    set /p "PASSWD=Password for account %ACCOUNT%: "
)
if "%PASSWD%"=="" (
    echo [ERROR] Password cannot be empty.
    pause
    exit /b 1
)

rem ---------- 1. account ----------
echo.
echo [1/4] Account "%ACCOUNT%" ...

net user "%ACCOUNT%" >nul 2>&1
if errorlevel 1 (
    net user "%ACCOUNT%" "%PASSWD%" /add >nul
    if errorlevel 1 (
        echo       [FAILED] Could not create the account.
        echo                Usually the password does not meet the local policy
        echo                ^(run "net accounts" to see the requirements^).
        goto :fail
    )
    echo       Account created
) else (
    net user "%ACCOUNT%" "%PASSWD%" >nul
    if errorlevel 1 (
        echo       [FAILED] Account exists but resetting the password FAILED.
        echo                The password is NOT what you typed - check "net accounts".
        goto :fail
    )
    echo       Account already existed - password reset OK
)

net user "%ACCOUNT%" /expires:never >nul 2>&1
net user "%ACCOUNT%" /passwordchg:no >nul 2>&1

where wmic >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Set-LocalUser -Name '%ACCOUNT%' -PasswordNeverExpires $true" >nul 2>&1
) else (
    wmic useraccount where "name='%ACCOUNT%'" set PasswordExpires=false >nul 2>&1
)
echo       Password set to never expire

rem Performance Monitor Users is REQUIRED: without it the WMI performance
rem class cannot be read and CPU will show 0 forever while memory and disk
rem look perfectly fine.
net localgroup "Performance Monitor Users" "%ACCOUNT%" /add >nul 2>&1
if errorlevel 1 (
    echo       Performance Monitor Users  - already a member
) else (
    echo       Performance Monitor Users  - added
)

rem Administrators is NOT required over SSH (commands run locally), but it
rem is added by default because it is harmless here and keeps this account
rem usable for the WMI path too. See tools\README.md if you want to drop it.
net localgroup Administrators "%ACCOUNT%" /add >nul 2>&1
if errorlevel 1 (
    echo       Administrators             - already a member
) else (
    echo       Administrators             - added
)

rem ---------- 2. install OpenSSH ----------
echo.
echo [2/4] OpenSSH Server ...

if not exist "%SRC%\sshd.exe" (
    echo       [SKIPPED] "%SRC%" not found.
    echo                 Copy the whole tools folder ^(with OpenSSH-Win64^)
    echo                 to this machine, or install OpenSSH manually.
    goto :after_openssh
)

if exist "%DST%\sshd.exe" (
    echo       Already installed at "%DST%"
) else (
    if not exist "%DST%" mkdir "%DST%"
    xcopy /y /e /q "%SRC%\*" "%DST%\" >nul
    if errorlevel 1 (
        echo       [FAILED] Could not copy OpenSSH to "%DST%".
        goto :fail
    )
    echo       Copied to "%DST%"
)

rem register the services (sshd + ssh-agent)
sc query sshd >nul 2>&1
if errorlevel 1 (
    pushd "%DST%"
    powershell -NoProfile -ExecutionPolicy Bypass -File "%DST%\install-sshd.ps1" >nul 2>&1
    popd
    sc query sshd >nul 2>&1
    if errorlevel 1 (
        echo       install-sshd.ps1 failed - creating services manually
        sc create sshd binPath= "\"%DST%\sshd.exe\"" start= auto DisplayName= "OpenSSH SSH Server" >nul
        sc create ssh-agent binPath= "\"%DST%\ssh-agent.exe\"" start= auto DisplayName= "OpenSSH Authentication Agent" >nul
        "%DST%\ssh-keygen.exe" -A >nul 2>&1
    )
)

rem ---------- 3. service + firewall ----------
:after_openssh
echo.
echo [3/4] Service and firewall ...

sc config sshd start= auto >nul 2>&1
net start sshd >nul 2>&1
sc query sshd | findstr /i "RUNNING" >nul
if errorlevel 1 (
    echo       [FAILED] sshd is not running. Check that the binaries are in
    echo                "%DST%" and try "net start sshd" manually.
    goto :fail
)
echo       sshd service is RUNNING ^(startup: automatic^)

netsh advfirewall firewall show rule name="OpenSSH-Server-In-TCP" >nul 2>&1
if errorlevel 1 (
    netsh advfirewall firewall add rule name="OpenSSH-Server-In-TCP" dir=in action=allow protocol=TCP localport=22 >nul
    echo       Firewall: opened TCP 22
) else (
    echo       Firewall: TCP 22 rule already exists
)

rem ---------- 4. self check ----------
echo.
echo [4/4] Self check ...

netstat -an | findstr /r /c:"TCP.*:22 .*LISTENING" >nul
if errorlevel 1 (
    echo       [WARN] Nothing is listening on port 22 yet.
    echo              Wait a few seconds and re-run, or check the sshd service.
) else (
    echo       Listening on port 22
)

net localgroup "Performance Monitor Users" 2>nul | findstr /i /c:"%ACCOUNT%" >nul
if errorlevel 1 (
    echo       [WARN] "%ACCOUNT%" is NOT in Performance Monitor Users.
    echo              CPU usage will read as 0. Add it manually.
) else (
    echo       Group membership verified
)

echo.
echo ==================================================================
echo   Done
echo ==================================================================
echo.
echo On the monitoring side:
echo   1. Add server
echo   2. OS type     : Windows
echo   3. Channel     : SSH
echo   4. Address     : this machine's LAN IP
echo   5. Username    : %ACCOUNT%
echo   6. Password    : the one set above
echo   7. Click "Test connection"
echo.
echo NOTE: if the monitor runs ON THIS machine, use 127.0.0.1 and leave
echo       BOTH username and password EMPTY.
echo.
pause
exit /b 0

:fail
echo.
echo Configuration did NOT complete. Fix the issue above and retry.
echo.
pause
exit /b 1
