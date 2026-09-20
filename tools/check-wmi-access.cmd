@echo off
setlocal

rem ==================================================================
rem  Diagnose why remote WMI access is denied.
rem  Run this ON THE TARGET SERVER, as administrator.
rem  Output is ASCII-only on purpose (UTF-8 batch files mis-parse).
rem ==================================================================

set "ACCOUNT=%~1"
if "%ACCOUNT%"=="" set "ACCOUNT=monitor"

net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Run as administrator.
    pause
    exit /b 1
)

echo.
echo ==================================================================
echo  WMI access diagnostics - account "%ACCOUNT%"
echo  Host: %COMPUTERNAME%     %DATE% %TIME%
echo ==================================================================

echo.
echo --- [1] Account state -------------------------------------------------
rem Lockout=True means repeated failed attempts have locked the account
rem (our own "Test connection" clicks can do this - threshold is often 10).
wmic useraccount where "name='%ACCOUNT%'" get Name,Disabled,Lockout,PasswordRequired,PasswordExpires /format:list 2>nul
if errorlevel 1 (
    rem wmic is absent on Windows 11 / Server 2025 - fall back to PowerShell
    powershell -NoProfile -Command "Get-LocalUser -Name '%ACCOUNT%' -ErrorAction SilentlyContinue | Select-Object Name,Enabled,LockedOut,PasswordExpires | Format-List" 2>nul
    if errorlevel 1 echo   (could not query the account)
)

echo.
echo --- [2] Group membership ----------------------------------------------
echo   Administrators:
net localgroup Administrators 2>nul | findstr /i /c:"%ACCOUNT%" || echo     NOT A MEMBER
echo   Performance Monitor Users:
net localgroup "Performance Monitor Users" 2>nul | findstr /i /c:"%ACCOUNT%" || echo     NOT A MEMBER

echo.
echo --- [3] Network access model (ForceGuest) -----------------------------
echo   [0 or absent = Classic / OK]   [1 = Guest only / BREAKS remote local accounts]
reg query "HKLM\SYSTEM\CurrentControlSet\Control\Lsa" /v ForceGuest 2>nul || echo   ForceGuest = absent (Classic - OK)

echo.
echo --- [4] UAC remote token filtering ------------------------------------
echo   [1 = filtering disabled / REQUIRED for remote local accounts]
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v LocalAccountTokenFilterPolicy 2>nul || echo   NOT SET - remote local accounts will be stripped of admin rights

echo.
echo --- [5] DCOM machine-wide permissions (decoded) -----------------------
echo   Who is allowed to remotely access / launch COM objects on this host.
echo   Hardened servers often strip Administrators here, which denies WMI.
powershell -NoProfile -Command "$p='HKLM:\SOFTWARE\Microsoft\Ole'; foreach($n in 'MachineAccessRestriction','MachineLaunchRestriction'){ $b=(Get-ItemProperty $p -Name $n -ErrorAction SilentlyContinue).$n; if($b){ Write-Host ('  '+$n+':'); try { $sd=New-Object System.Security.AccessControl.RawSecurityDescriptor($b,0); foreach($a in $sd.DiscretionaryAcl){ $t=$a.SecurityIdentifier.Value; try { $t=$a.SecurityIdentifier.Translate([System.Security.Principal.NTAccount]).Value } catch {}; Write-Host ('    '+$t+'  '+$a.AccessControlType) } } catch { Write-Host '    (could not decode)' } } else { Write-Host ('  '+$n+': not set (defaults apply - OK)') } }" 2>nul
if errorlevel 1 echo   (could not decode)

echo.
echo --- [6] WMI service ---------------------------------------------------
sc query Winmgmt | findstr /i "STATE"

echo.
echo --- [7] Firewall - WMI rules enabled? ---------------------------------
rem PowerShell is used here because netsh output is localised, so text
rem filtering on "Enabled" would fail on non-English Windows.
rem "-Command" is not affected by ExecutionPolicy.
powershell -NoProfile -Command "Get-NetFirewallRule -DisplayName '*Windows Management Instrumentation*','ServerMonitor-WMI*' -ErrorAction SilentlyContinue | Select-Object DisplayName,Direction,Enabled | Format-Table -AutoSize" 2>nul
if errorlevel 1 echo   (could not query firewall rules)

echo.
echo --- [8] Local account policy (raw, may be localised) ------------------
net accounts 2>nul

echo.
echo --- [9] Logon events for "%ACCOUNT%" ----------------------------------
echo   4624 = logon SUCCESS, 4625 = logon FAILURE.
echo   Empty output means logon auditing is OFF (the Windows Server default),
echo   in which case this section tells us nothing - see the note below.
wevtutil qe Security /q:"*[System[(EventID=4624 or EventID=4625)]]" /c:60 /rd:true /f:text 2>nul | findstr /i /c:"%ACCOUNT%"

echo.
echo   TO TURN ON LOGON AUDITING (the decisive test - do this, then click
echo   "Test connection" in the monitor, then run this script again):
echo.
echo     auditpol /set /subcategory:"{0CCE9215-69AE-11D9-BED3-505054503030}" /success:enable /failure:enable
echo.
echo   Using the GUID avoids the localised subcategory name. Then:
echo     4625 appears  =^> authentication itself is failing (password/account)
echo     4624 appears  =^> authentication SUCCEEDED, but authorisation is
echo                       denied afterwards (DCOM / WMI namespace / NTLM policy)

echo.
echo --- [10] NTLM restrictions (common hardening that breaks WMI) ---------
echo   Local accounts authenticate over NTLM. If NTLM is restricted or
echo   disabled by a security baseline, remote WMI fails with Access Denied.
echo   [RestrictReceivingNTLMTraffic: 0=allow all, 1=allow domain only, 2=DENY ALL]
reg query "HKLM\SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0" /v RestrictReceivingNTLMTraffic 2>nul || echo   RestrictReceivingNTLMTraffic = not set (allow - OK)
reg query "HKLM\SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0" /v RestrictSendingNTLMTraffic 2>nul || echo   RestrictSendingNTLMTraffic = not set (OK)
echo   [LmCompatibilityLevel: 3-5 are normal; missing = default]
reg query "HKLM\SYSTEM\CurrentControlSet\Control\Lsa" /v LmCompatibilityLevel 2>nul || echo   LmCompatibilityLevel = not set (default - OK)

echo.
echo --- [11] Other hardening that can block remote admin ------------------
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v FilterAdministratorToken 2>nul || echo   FilterAdministratorToken = not set (OK)
reg query "HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters" /v RestrictNullSessAccess 2>nul || echo   RestrictNullSessAccess = not set (OK)

echo.
echo --- [12] Is this machine inside a domain? -----------------------------
echo   If it is, a DOMAIN account is strongly preferred: domain accounts are
echo   not affected by LocalAccountTokenFilterPolicy or local NTLM restrictions.
powershell -NoProfile -Command "$cs=Get-CimInstance Win32_ComputerSystem; Write-Host ('  PartOfDomain : '+$cs.PartOfDomain); Write-Host ('  Domain       : '+$cs.Domain)" 2>nul
if errorlevel 1 echo   (could not query)

echo.
echo ==================================================================
echo  Done. Copy ALL of the above and send it back for analysis.
echo ==================================================================
echo.
pause
