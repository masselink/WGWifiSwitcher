@echo off
title WireGuard Client and WiFi Switcher -- Build
setlocal enabledelayedexpansion
echo.
echo  ====================================
echo   WireGuard Client and WiFi Switcher
echo  ====================================
echo           v2.0  by Harold Masselink
echo  ====================================
echo           (Using Claude.ai)
echo  ====================================
echo.

rem -----------------------------------------------------------------------------
rem  Check .NET SDK
rem -----------------------------------------------------------------------------
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo  ERROR: .NET SDK not found.
    echo  Install .NET 10 SDK from: https://dotnet.microsoft.com/download/dotnet/10.0
    pause & exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo  .NET SDK detected: %DOTNET_VER%

for /f "tokens=1 delims=." %%m in ("%DOTNET_VER%") do set DOTNET_MAJOR=%%m
if "%DOTNET_MAJOR%" == "10" goto dotnet_ok
if %DOTNET_MAJOR% GTR 10 goto dotnet_ok
echo.
echo  ERROR: .NET 10 SDK is required (detected: %DOTNET_VER%).
echo  Download from: https://dotnet.microsoft.com/download/dotnet/10.0
echo.
pause & exit /b 1
:dotnet_ok

rem -----------------------------------------------------------------------------
rem  Ask: download WireGuard DLLs?
rem -----------------------------------------------------------------------------
echo.
echo  -------------------------------------------------------
echo   WireGuard DLLs (tunnel.dll + wireguard.dll)
echo  -------------------------------------------------------
echo.
echo   Local tunnels need two DLLs placed next to the exe:
echo.
echo     wireguard.dll -- downloaded from WireGuard LLC
echo     tunnel.dll    -- extracted from WireGuard MSI
echo.
echo   No Go, no compiler, no extra tools needed.
echo   Downloads ~10 MB from download.wireguard.com.
echo.
echo   Without them, only imported WireGuard-client tunnels work.
echo.

set /p BUILD_DLLS="  Download and include DLLs? [Y/N]: "
if /i "!BUILD_DLLS!"=="Y"   goto download_dlls
if /i "!BUILD_DLLS!"=="YES" goto download_dlls
goto build_app

rem -----------------------------------------------------------------------------
rem  Download DLLs via PowerShell (no build tools needed)
rem -----------------------------------------------------------------------------
:download_dlls
echo.
set DEPS=%~dp0deps
set DIST=%~dp0dist
if not exist "!DEPS!" mkdir "!DEPS!"

rem --- Step 1: wireguard.dll from wireguard-nt zip ----------------------------
echo  [1/2] Downloading wireguard.dll (wireguard-nt)...

echo         Finding latest version...
powershell -NoProfile -Command ^
  "try { $p=(Invoke-WebRequest 'https://download.wireguard.com/wireguard-nt/' -UseBasicParsing).Content; $m=[regex]::Matches($p,'wireguard-nt-([\d.]+)\.zip'); $v=$m|ForEach-Object{$_.Groups[1].Value}|Sort-Object{[version]$_}|Select-Object -Last 1; if($v){Set-Content '!DEPS!\wg_nt_ver.txt' $v}else{exit 1} } catch { exit 1 }"
if errorlevel 1 (
    echo  ERROR: Could not reach download.wireguard.com
    echo  Check internet connection and try again.
    goto fail
)
set /p WG_NT_VER=<!DEPS!\wg_nt_ver.txt
echo         Latest wireguard-nt: %WG_NT_VER%

set WG_NT_ZIP=!DEPS!\wireguard-nt-%WG_NT_VER%.zip
set WG_NT_DIR=!DEPS!\wireguard-nt-%WG_NT_VER%
rem The zip contains a wireguard-nt\ subfolder, so dll lands at wireguard-nt\bin\amd64\wireguard.dll
set WG_NT_DLL=!WG_NT_DIR!\wireguard-nt\bin\amd64\wireguard.dll

if exist "!WG_NT_DLL!" (
    echo         Already downloaded.
) else (
    echo         Downloading wireguard-nt-%WG_NT_VER%.zip...
    powershell -NoProfile -Command ^
      "try { Invoke-WebRequest 'https://download.wireguard.com/wireguard-nt/wireguard-nt-%WG_NT_VER%.zip' -OutFile '!WG_NT_ZIP!' -UseBasicParsing } catch { exit 1 }"
    if errorlevel 1 ( echo  ERROR: Download failed. & goto fail )
    echo         Extracting...
    powershell -NoProfile -Command ^
      "Expand-Archive -Path '!WG_NT_ZIP!' -DestinationPath '!WG_NT_DIR!' -Force"
    del "!WG_NT_ZIP!" 2>nul
    rem Search for wireguard.dll anywhere under the extract dir in case structure differs
    if not exist "!WG_NT_DLL!" (
        powershell -NoProfile -Command ^
          "$f=(Get-ChildItem '!WG_NT_DIR!' -Recurse -Filter 'wireguard.dll' | Where-Object {$_.DirectoryName -like '*amd64*'} | Select-Object -First 1).FullName; if($f){Copy-Item $f '!WG_NT_DIR!\wireguard.dll'}else{exit 1}"
        if errorlevel 1 ( echo  ERROR: wireguard.dll not found inside zip. & goto fail )
        set WG_NT_DLL=!WG_NT_DIR!\wireguard.dll
    )
)

if not exist "!WG_NT_DLL!" (
    echo  ERROR: wireguard.dll not found after extraction.
    goto fail
)
echo         wireguard.dll ready (v%WG_NT_VER%)

rem --- Step 2: tunnel.dll from WireGuard Windows MSI --------------------------
echo.
echo  [2/2] Extracting tunnel.dll from WireGuard Windows MSI...

echo         Finding latest MSI version...
powershell -NoProfile -Command ^
  "try { $p=(Invoke-WebRequest 'https://download.wireguard.com/windows-client/' -UseBasicParsing).Content; $m=[regex]::Matches($p,'wireguard-amd64-([\d.]+)\.msi'); $v=$m|ForEach-Object{$_.Groups[1].Value}|Sort-Object{[version]$_}|Select-Object -Last 1; if($v){Set-Content '!DEPS!\wg_win_ver.txt' $v}else{exit 1} } catch { exit 1 }"
if errorlevel 1 (
    echo  ERROR: Could not determine latest WireGuard Windows version.
    goto fail
)
set /p WG_WIN_VER=<!DEPS!\wg_win_ver.txt
echo         Latest WireGuard Windows: %WG_WIN_VER%

set WG_MSI=!DEPS!\wireguard-amd64-%WG_WIN_VER%.msi
set WG_MSI_EXTRACT=!DEPS!\wireguard-amd64-%WG_WIN_VER%-extracted
set TUNNEL_DLL=!WG_MSI_EXTRACT!\PFiles64\WireGuard\tunnel.dll

if exist "!TUNNEL_DLL!" (
    echo         Already extracted.
) else (
    if not exist "!WG_MSI!" (
        echo         Downloading wireguard-amd64-%WG_WIN_VER%.msi...
        powershell -NoProfile -Command ^
          "try { Invoke-WebRequest 'https://download.wireguard.com/windows-client/wireguard-amd64-%WG_WIN_VER%.msi' -OutFile '!WG_MSI!' -UseBasicParsing } catch { exit 1 }"
        if errorlevel 1 ( echo  ERROR: MSI download failed. & goto fail )
    )
    echo         Extracting MSI (no installation, may take 20s)...
    if not exist "!WG_MSI_EXTRACT!" mkdir "!WG_MSI_EXTRACT!"
    powershell -NoProfile -Command ^
      "Start-Process msiexec -ArgumentList '/a','!WG_MSI!','/qn','TARGETDIR=!WG_MSI_EXTRACT!' -Wait -PassThru | Out-Null"
)

if not exist "!TUNNEL_DLL!" (
    echo         Searching for tunnel.dll in extracted MSI...
    powershell -NoProfile -Command ^
      "$f=(Get-ChildItem '!WG_MSI_EXTRACT!' -Recurse -Filter 'tunnel.dll' | Select-Object -First 1).FullName; if($f){Copy-Item $f '!WG_MSI_EXTRACT!\tunnel.dll';Write-Host $f}else{exit 1}"
    if errorlevel 1 (
        echo  ERROR: tunnel.dll not found in MSI. Expected at: !TUNNEL_DLL!
        goto fail
    )
    set TUNNEL_DLL=!WG_MSI_EXTRACT!\tunnel.dll
)
echo         tunnel.dll ready (from WireGuard v%WG_WIN_VER%)
set DO_COPY_DLLS=1

rem -----------------------------------------------------------------------------
rem  Build WGClientWifiSwitcher
rem -----------------------------------------------------------------------------
:build_app
echo.
echo  Publishing WGClientWifiSwitcher (single-file, win-x64)...
echo.
dotnet publish "%~dp0WGClientWifiSwitcher.csproj" -c Release -o "!DIST!"
if errorlevel 1 (
    echo.
    echo  BUILD FAILED. See output above.
    pause & exit /b 1
)

rem -----------------------------------------------------------------------------
rem  Copy DLLs into dist if downloaded
rem -----------------------------------------------------------------------------
if "!DO_COPY_DLLS!"=="1" (
    echo.
    echo  Copying DLLs to dist\...
    copy /Y "!WG_NT_DLL!"  "!DIST!\wireguard.dll" >nul
    if errorlevel 1 ( echo  ERROR: Failed to copy wireguard.dll & goto fail )
    copy /Y "!TUNNEL_DLL!" "!DIST!\tunnel.dll"    >nul
    if errorlevel 1 ( echo  ERROR: Failed to copy tunnel.dll & goto fail )
    echo         dist\wireguard.dll  (wireguard-nt v%WG_NT_VER%)
    echo         dist\tunnel.dll     (WireGuard for Windows v%WG_WIN_VER%)
)

rem -----------------------------------------------------------------------------
rem  Summary
rem -----------------------------------------------------------------------------
if exist "!DIST!\WGClientWifiSwitcher.exe" (
    echo.
    echo  ==========================================
    echo   BUILD SUCCESSFUL
    echo  ==========================================
    echo.
    echo   dist\WGClientWifiSwitcher.exe
    echo   dist\lang\
    if "!DO_COPY_DLLS!"=="1" (
        echo   dist\tunnel.dll     ^(WireGuard v%WG_WIN_VER%^)
        echo   dist\wireguard.dll  ^(wireguard-nt v%WG_NT_VER%^)
    ) else (
        echo.
        echo   NOTE: DLLs not included. For local tunnel support,
        echo   place tunnel.dll + wireguard.dll next to the exe.
        echo   See Settings for download links.
    )
    echo.
    echo   Target machine requires .NET 10 Desktop Runtime:
    echo   https://dotnet.microsoft.com/download/dotnet/10.0
) else (
    echo  ERROR: exe not found after build.
    pause & exit /b 1
)
pause
exit /b 0

:fail
echo.
echo  ==========================================
echo   BUILD FAILED
echo  ==========================================
echo.
pause
exit /b 1
