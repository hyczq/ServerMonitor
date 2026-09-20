@echo off
rem NOTE: deliberately NOT using "enabledelayedexpansion" - it would
rem mangle passwords containing the "!" character.
setlocal

rem ==================================================================
rem  Configure a Windows server as a WMI monitoring target
rem  for ServerMonitor.
rem
rem  Pure CMD -- no PowerShell needed:
rem    * works regardless of PowerShell version
rem    * not affected by ExecutionPolicy
rem    * no script encoding problems
rem
rem  Output is ASCII-only ON PURPOSE. Batch files written in UTF-8
rem  are mis-parsed by cmd.exe: the trailing bytes of multi-byte
rem  characters collide with cmd metacharacters such as & and |.
rem
rem  Usage: right-click this file -> "Run as administrator"
rem  (edit the two lines below first, or just run it and it will ask)
rem
rem  Tested on Windows Server 2012 R2 and later.
rem ==================================================================

rem IMPORTANT: use the quoted "set VAR=value" form. Without the quotes a
rem password containing & | < > ^ would be split by cmd and silently
rem stored WRONG (verified: "set P=Ab#3&xY" stored only "Ab#3").
rem PASSWD is deliberately left empty so the script asks for it.
rem A hard-coded default password in a public repo means everyone who
rem runs this gets the same known credential.
set "ACCOUNT=monitor"
set "PASSWD="

rem ---------- 0. require administrator ----------
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
echo   Configure WMI monitoring target
echo ==================================================================
echo.

rem ask for the password interactively if not preset
if "%PASSWD%"=="" (
    set /p "PASSWD=Password for account %ACCOUNT%: "
)
if "%PASSWD%"=="" (
    echo [ERROR] Password cannot be empty.
    pause
    exit /b 1
)

rem ---------- 1. firewall + remote access ----------
echo [1/4] Firewall ^& remote access ...

rem --- UAC remote token filtering ---
rem
rem Since Vista, when a LOCAL account (not a domain account) connects from
rem another machine, Windows strips its administrator rights from the
rem token - even if the account IS in Administrators. Result: remote WMI
rem fails with "Access denied" while every local check passes.
rem
rem LocalAccountTokenFilterPolicy = 1 disables that filtering.
rem Without it, remote WMI with a local account simply cannot work.
rem Trade-off: it makes local accounts full admins remotely, so use a
rem dedicated monitoring account (which is what this script creates).
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f >nul 2>&1
if errorlevel 1 (
    echo       [WARN] Could not set LocalAccountTokenFilterPolicy.
    echo              Remote WMI with a local account will fail with
    echo              "Access denied". Set it manually, or use a domain
    echo              account / the built-in Administrator instead.
) else (
    echo       Disabled UAC remote token filtering ^(LocalAccountTokenFilterPolicy=1^)
)


rem Prefer the built-in WMI rule group. On Chinese Windows the group
rem name is still English (verified), so this works on both locales.
netsh advfirewall firewall set rule group="Windows Management Instrumentation (WMI)" new enable=yes >nul 2>&1
if errorlevel 1 (
    echo       Built-in WMI rule group not found - adding explicit port rules
    netsh advfirewall firewall add rule name="ServerMonitor-WMI-135" dir=in action=allow protocol=TCP localport=135 >nul 2>&1
    netsh advfirewall firewall add rule name="ServerMonitor-WMI-RPC" dir=in action=allow protocol=TCP localport=49152-65535 >nul 2>&1
    echo       Added: TCP 135 and TCP 49152-65535
) else (
    echo       Enabled built-in WMI inbound rules ^(135 + RPC dynamic ports^)
)

rem ---------- 2. account ----------
echo.
echo [2/4] Account "%ACCOUNT%" ...

net user "%ACCOUNT%" >nul 2>&1
if errorlevel 1 (
    net user "%ACCOUNT%" "%PASSWD%" /add >nul
    if errorlevel 1 (
        echo       [FAILED] Could not create the account.
        echo                Usually the password does not meet the complexity
        echo                policy: at least 6 chars, using 3 of these 4 -
        echo                uppercase, lowercase, digit, symbol.
        goto :fail
    )
    echo       Account created
) else (
    net user "%ACCOUNT%" "%PASSWD%" >nul
    if errorlevel 1 (
        echo       [FAILED] Account exists but resetting the password FAILED.
        echo                The password is NOT what you typed. Most likely
        echo                the local password policy rejected it - run
        echo                "net accounts" and check the length/complexity
        echo                requirements, or set it via compmgmt.msc.
        goto :fail
    )
    echo       Account already existed - password reset OK
)

net user "%ACCOUNT%" /expires:never >nul 2>&1
net user "%ACCOUNT%" /passwordchg:no >nul 2>&1

rem Password-never-expires is not settable via "net user".
rem wmic does it on 2012 R2; newer Windows removed wmic, so fall back
rem to PowerShell -Command (unaffected by ExecutionPolicy).
where wmic >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Set-LocalUser -Name '%ACCOUNT%' -PasswordNeverExpires $true" >nul 2>&1
) else (
    wmic useraccount where "name='%ACCOUNT%'" set PasswordExpires=false >nul 2>&1
)
if errorlevel 1 (
    echo       [WARN] Could not set "password never expires".
    echo              When the password expires, monitoring will stop.
) else (
    echo       Password set to never expire
)

rem ---------- 3. group membership ----------
echo.
echo [3/4] Group membership ...

rem Administrators: required for remote WMI access
net localgroup Administrators "%ACCOUNT%" /add >nul 2>&1
if errorlevel 1 (
    echo       Administrators             - already a member
) else (
    echo       Administrators             - added
)

rem Performance Monitor Users: required to read CPU performance counters.
rem Missing this is easy to overlook - memory and disk look fine but
rem CPU stays at 0 forever.
net localgroup "Performance Monitor Users" "%ACCOUNT%" /add >nul 2>&1
if errorlevel 1 (
    echo       Performance Monitor Users  - already a member
) else (
    echo       Performance Monitor Users  - added
)

rem ---------- 4. self check ----------
echo.
echo [4/4] Self check ...

rem findstr rather than find: "find" is a common name that can be
rem shadowed by other tools on PATH, which silently breaks the check.
net localgroup "Performance Monitor Users" 2>nul | findstr /i /c:"%ACCOUNT%" >nul
if errorlevel 1 (
    echo       [WARN] "%ACCOUNT%" is NOT in Performance Monitor Users.
    echo              CPU usage will read as 0. Add it manually.
) else (
    echo       Group membership verified
)

rem verify the WMI performance class is queryable
powershell -NoProfile -Command "if (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter \"Name='_Total'\" -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }" >nul 2>&1
if errorlevel 1 (
    echo       [WARN] WMI performance counters are not readable.
    echo              Run "winmgmt /verifyrepository" on this machine.
) else (
    echo       WMI performance counters readable
)

echo.
echo ==================================================================
echo   Done
echo ==================================================================
echo.
echo On the monitoring side:
echo   1. Add server -^> OS type "Windows" -^> channel "Auto"
echo   2. Address: this machine's LAN IP
echo   3. Username "%ACCOUNT%", password as just set
echo   4. Click "Test connection"
echo.
echo NOTE: if the monitor runs ON THIS machine, use 127.0.0.1 as the
echo       address and leave BOTH username and password EMPTY.
echo       WMI forbids credentials on local connections.
echo.
pause
exit /b 0

:fail
echo.
echo Configuration did NOT complete. Fix the issue above and retry.
echo.
pause
exit /b 1
