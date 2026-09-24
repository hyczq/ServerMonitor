@echo off
chcp 936 >nul
rem 注意：刻意不开 enabledelayedexpansion —— 它会把含有 "!" 的口令改坏。
setlocal

rem ==================================================================
rem  把一台 Windows 服务器配置成「服务器监控台」的 SSH 采集目标机
rem                                             *** 推荐走这条路线 ***
rem
rem  为什么 SSH 比 WMI 稳：
rem    · 命令在目标机本地执行，远程 DCOM、UAC 令牌降权、RPC 动态端口
rem      这一整条最容易出问题的链路完全不经过。
rem    · 只需要开放一个端口（22）。
rem    · 采集账号不需要管理员权限，Performance Monitor Users 就够读到
rem      CPU 计数器。
rem
rem  脚本做四件事：
rem    1. 创建采集账号
rem    2. 从 .\OpenSSH-Win64 安装 OpenSSH Server
rem    3. 放行防火墙并启动服务
rem    4. 自检（端口、组成员关系）
rem
rem  用法：右键 -> 「以管理员身份运行」
rem  需要把整个 tools 目录（含 OpenSSH-Win64）都拷到目标机上。
rem
rem  ---------------------------------------------------------------
rem  【重要】本文件必须保持 GBK（cp936）编码。
rem  批处理文件的编码一旦和 cmd.exe 的代码页对不上，中文的尾字节就会撞上
rem  & | < > 这些元字符，注释行会被当成命令执行（实测确认）。
rem  用编辑器改完请务必按「ANSI / GBK」保存，不要存成 UTF-8。
rem  ---------------------------------------------------------------
rem ==================================================================

rem ---- 默认值 ----
rem ACCOUNT  采集账号名，可以改。
rem DEFPASS  直接回车时采用的默认口令。改这一行即可。
rem           【重要】本仓库是公开的，所以这里写死什么，就等于对外公开了什么。
rem             正式环境请务必改成自己的口令。
rem           运行时会提示输入口令；直接回车 = 采用这里的 DEFPASS。
set "ACCOUNT=monitor"
set "DEFPASS=Srv@2026#Lan"

rem 口令一律运行时输入，不再预填；下面的提示允许直接回车取默认值。
set "PASSWD="

rem 必须用带引号的 "set VAR=value" 形式：不加引号时，含有 & | < > ^ 的
rem 口令会被 cmd 拆开，静默存成错误的值（实测 "Ab#3&xY" 只存下了 "Ab#3"）。
set "SRC=%~dp0OpenSSH-Win64"
set "DST=%ProgramFiles%\OpenSSH"

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [错误] 需要管理员权限。
    echo        请右键本文件，选择「以管理员身份运行」。
    echo.
    pause
    exit /b 1
)

echo.
echo ==================================================================
echo   配置 SSH 采集目标机
echo   主机：%COMPUTERNAME%     %DATE% %TIME%
echo ==================================================================

rem 提示输入口令；什么都不输、直接回车，就采用上面的 DEFPASS。
rem 上一行已经把它清空了：set /p 遇到空输入时行为不统一（置空或保持原值），
rem 提前清掉才能保证「直接回车」一定落到默认口令上。
set /p "PASSWD=请输入账号 %ACCOUNT% 的口令（直接回车用默认口令 %DEFPASS%）: "
if "%PASSWD%"=="" set "PASSWD=%DEFPASS%"

rem 只有 DEFPASS 也被清空、而且又按了回车，才会走到这里
if "%PASSWD%"=="" (
    echo [错误] 口令不能为空，且脚本里的 DEFPASS 也是空的。
    echo        请编辑本脚本开头的 DEFPASS 一行，或重新运行并手动输入。
    pause
    exit /b 1
)

rem 刚才多半是按了回车 —— 明确提醒一次
if /i "%PASSWD%"=="%DEFPASS%" (
    echo.
    echo ****************************************************************
    echo   [警告] 正在使用脚本内置的默认口令。
    echo          本仓库是公开的，该口令等同于一个公开的凭据 ——
    echo          任何拿到本仓库的人都能用它登录这台机器。
    echo          正式环境请改掉：编辑本脚本开头的 DEFPASS 一行，
    echo          或者重新运行本脚本，手动输入一个口令。
    echo ****************************************************************
)

rem ---------- 1. 账号 ----------
echo.
echo [1/4] 账号 %ACCOUNT% ...

net user "%ACCOUNT%" >nul 2>&1
if errorlevel 1 (
    net user "%ACCOUNT%" "%PASSWD%" /add >nul
    if errorlevel 1 (
        echo       [失败] 账号创建失败。
        echo              通常是口令不满足本机密码策略
        echo              （运行 net accounts 查看具体要求）。
        goto :fail
    )
    echo       账号已创建
) else (
    net user "%ACCOUNT%" "%PASSWD%" >nul
    if errorlevel 1 (
        echo       [失败] 账号已存在，但重设口令失败。
        echo              说明这个口令没被接受，请检查 net accounts 的策略。
        goto :fail
    )
    echo       账号已存在，口令已重设
)

net user "%ACCOUNT%" /expires:never >nul 2>&1
net user "%ACCOUNT%" /passwordchg:no >nul 2>&1

rem 「口令永不过期」没法用 net user 设置：wmic 在 2012 R2 上可用，
rem 新版 Windows 已移除 wmic，所以回退到 PowerShell -Command
rem （走 -Command 不受 ExecutionPolicy 影响）。
where wmic >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Set-LocalUser -Name '%ACCOUNT%' -PasswordNeverExpires $true" >nul 2>&1
) else (
    wmic useraccount where "name='%ACCOUNT%'" set PasswordExpires=false >nul 2>&1
)
echo       口令已设为永不过期

rem Performance Monitor Users 这一组是必须的：少了它，WMI 性能类读不出来，
rem 症状是 CPU 恒为 0，而内存和磁盘看起来完全正常。
net localgroup "Performance Monitor Users" "%ACCOUNT%" /add >nul 2>&1
if errorlevel 1 (
    echo       Performance Monitor Users  - 已在组内
) else (
    echo       Performance Monitor Users  - 已加入
)

rem 走 SSH 时其实不需要管理员权限（命令是本地执行的），默认加进去是因为
rem 这样无害，而且同一个账号还能顺带兼容 WMI 路线。想降权见 tools\README.md。
net localgroup Administrators "%ACCOUNT%" /add >nul 2>&1
if errorlevel 1 (
    echo       Administrators             - 已在组内
) else (
    echo       Administrators             - 已加入
)

rem ---------- 2. 安装 OpenSSH ----------
echo.
echo [2/4] OpenSSH Server ...

if not exist "%SRC%\sshd.exe" (
    echo       [跳过] 找不到 "%SRC%"。
    echo              请把整个 tools 目录（含 OpenSSH-Win64）拷到本机，
    echo              或者自己手工安装 OpenSSH。
    goto :after_openssh
)

if exist "%DST%\sshd.exe" (
    echo       已安装在 "%DST%"
) else (
    if not exist "%DST%" mkdir "%DST%"
    xcopy /y /e /q "%SRC%\*" "%DST%\" >nul
    if errorlevel 1 (
        echo       [失败] 无法把 OpenSSH 复制到 "%DST%"。
        goto :fail
    )
    echo       已复制到 "%DST%"
)

rem 注册服务（sshd 和 ssh-agent）
sc query sshd >nul 2>&1
if errorlevel 1 (
    pushd "%DST%"
    powershell -NoProfile -ExecutionPolicy Bypass -File "%DST%\install-sshd.ps1" >nul 2>&1
    popd
    sc query sshd >nul 2>&1
    if errorlevel 1 (
        echo       install-sshd.ps1 失败 —— 改为手工创建服务
        sc create sshd binPath= "\"%DST%\sshd.exe\"" start= auto DisplayName= "OpenSSH SSH Server" >nul
        sc create ssh-agent binPath= "\"%DST%\ssh-agent.exe\"" start= auto DisplayName= "OpenSSH Authentication Agent" >nul
        "%DST%\ssh-keygen.exe" -A >nul 2>&1
    )
)

rem ---------- 3. 服务与防火墙 ----------
:after_openssh
echo.
echo [3/4] 服务与防火墙 ...

sc config sshd start= auto >nul 2>&1
net start sshd >nul 2>&1
sc query sshd | findstr /i "RUNNING" >nul
if errorlevel 1 (
    echo       [失败] sshd 没有运行。请确认程序在 "%DST%" 下，
    echo              然后手工执行 net start sshd 试试。
    goto :fail
)
echo       sshd 服务运行中（启动类型：自动）

netsh advfirewall firewall show rule name="OpenSSH-Server-In-TCP" >nul 2>&1
if errorlevel 1 (
    netsh advfirewall firewall add rule name="OpenSSH-Server-In-TCP" dir=in action=allow protocol=TCP localport=22 >nul
    echo       防火墙：已放行 TCP 22
) else (
    echo       防火墙：TCP 22 规则已存在
)

rem ---------- 4. 自检 ----------
echo.
echo [4/4] 自检 ...

netstat -an | findstr /r /c:"TCP.*:22 .*LISTENING" >nul
if errorlevel 1 (
    echo       [警告] 22 端口还没有在监听。
    echo              等几秒重跑一次，或者检查 sshd 服务。
) else (
    echo       22 端口正在监听
)

net localgroup "Performance Monitor Users" 2>nul | findstr /i /c:"%ACCOUNT%" >nul
if errorlevel 1 (
    echo       [警告] %ACCOUNT% 不在 Performance Monitor Users 组里。
    echo              CPU 会一直显示 0，请手工加进去。
) else (
    echo       组成员关系已确认
)

echo.
echo ==================================================================
echo   配置完成
echo ==================================================================
echo.
echo 接下来在监控端：
echo   1. 添加服务器
echo   2. 系统类型 ：Windows
echo   3. 采集通道 ：SSH
echo   4. 地址     ：这台机器的内网 IP
echo   5. 用户名   ：%ACCOUNT%
rem 这里必须用 for 取出来，不能直接 echo %PASSWD%：
rem 口令里含 & | < > 时会被 cmd 当成元字符拆开（实测 "Ab#3&xY" 只回显了
rem "Ab#3"，后半截还被当成命令执行报错）。for /f 把整串当一个字符串，
rem 再交给 %%~P 输出，既不用加引号也不会破坏内容。
for /f "delims=" %%P in ("%PASSWD%") do echo   6. 口令     ：%%~P
echo   7. 点「测试连接」
echo.
if /i "%PASSWD%"=="%DEFPASS%" (
    echo [提醒] 上面这个口令是脚本内置的默认口令，和本仓库一起公开。
    echo        正式环境请改掉：编辑脚本开头的 DEFPASS 一行，
    echo        或者重新运行本脚本，在提示处手动输入一个口令。
    echo.
)
echo 注意：如果监控程序就跑在这台机器上，地址填 127.0.0.1，
echo       并且把用户名和口令都留空（本机连接不允许指定凭据）。
echo.
pause
exit /b 0

:fail
echo.
echo 配置没有完成。请先解决上面的问题再重试。
echo.
pause
exit /b 1
