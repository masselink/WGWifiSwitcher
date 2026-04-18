@echo off
title MasselGUARD -- Build
setlocal enabledelayedexpansion
echo.
echo  ====================================
echo              MasselGUARD
echo  ====================================
echo       v2.3.1  by Harold Masselink
echo  ====================================
echo           (Using Claude.ai)
echo  ====================================
echo.

rem ── Step 1: verify .NET SDK ──────────────────────────────────────────────────
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo  ERROR: .NET SDK not found.
    echo  Install .NET 10 SDK from: https://dotnet.microsoft.com/download/dotnet/10.0
    pause & exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo  .NET SDK detected: %DOTNET_VER%

for /f "tokens=1 delims=." %%m in ("%DOTNET_VER%") do set DOTNET_MAJOR=%%m
if "%DOTNET_MAJOR%" == "10" goto sdk_ok
if %DOTNET_MAJOR% GTR 10 goto sdk_ok
echo.
echo  ERROR: .NET 10 SDK is required (detected: %DOTNET_VER%).
echo  Download from: https://dotnet.microsoft.com/download/dotnet/10.0
echo.
pause & exit /b 1

:sdk_ok
echo.

rem ── Step 2: compile the application ─────────────────────────────────────────
echo  -------------------------------------------------------
echo   Compiling MasselGUARD...
echo  -------------------------------------------------------
echo.
dotnet publish "%~dp0MasselGUARD.csproj" -c Release -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo  ==========================================
    echo   BUILD FAILED  -- fix errors before DLLs
    echo  ==========================================
    echo.
    pause & exit /b 1
)

if not exist "%~dp0dist\MasselGUARD.exe" (
    echo  ERROR: exe not found after publish.
    pause & exit /b 1
)

echo.
echo  Compile OK -- MasselGUARD.exe ready.
echo.

rem ── Step 3: copy theme folder into dist ──────────────────────────────────────
echo  -------------------------------------------------------
echo   Copying theme folder...
echo  -------------------------------------------------------
if exist "%~dp0theme" (
    if exist "%~dp0dist\theme" rmdir /s /q "%~dp0dist\theme"
    xcopy /e /i /q "%~dp0theme" "%~dp0dist\theme" >nul
    echo  Theme folder copied to dist\theme\
) else (
    echo  WARNING: theme folder not found -- skipped.
)
echo.

rem ── Step 4: DLLs ─────────────────────────────────────────────────────────────
echo  -------------------------------------------------------
echo   Local tunnel DLLs (tunnel.dll + wireguard.dll)
echo  -------------------------------------------------------
echo.
echo   Standalone local tunnels require tunnel.dll + wireguard.dll
echo   next to the exe. Choose how to obtain them:
echo.
echo   [1] Use provided DLLs  -- use the DLLs already included
echo                             next to BUILD.bat (recommended)
echo   [2] Build from source  -- compile tunnel.dll (needs Go + gcc)
echo   [3] Download           -- download pre-built DLLs from GitHub
echo   [4] Skip               -- add DLLs manually later
echo.

set DLL_CHOICE=
set /p DLL_CHOICE="  Choose [1/2/3/4]: "

if "!DLL_CHOICE!"=="1" goto use_provided
if "!DLL_CHOICE!"=="2" goto do_build
if "!DLL_CHOICE!"=="3" goto do_dl
goto dll_done

rem ── Option 1: use provided DLLs ─────────────────────────────────────────────
:use_provided
echo.
echo  Copying provided DLLs...
set DIST=%~dp0dist
set SRC=%~dp0
set OK=1

if exist "!SRC!tunnel.dll" (
    copy /y "!SRC!tunnel.dll" "!DIST!tunnel.dll" >nul
    echo    Copied: tunnel.dll
) else (
    echo  ERROR: tunnel.dll not found next to BUILD.bat.
    set OK=0
)

if exist "!SRC!wireguard.dll" (
    copy /y "!SRC!wireguard.dll" "!DIST!wireguard.dll" >nul
    echo    Copied: wireguard.dll
) else (
    echo  ERROR: wireguard.dll not found next to BUILD.bat.
    set OK=0
)

if "!OK!"=="1" (
    set DO_COPY_DLLS=1
    goto dll_done
)
echo.
echo  One or more provided DLLs were missing. Falling through to warning.
goto dll_warn

rem ── Option 2: build from source ─────────────────────────────────────────────
:do_build
set DEPS=%~dp0deps
set DIST=%~dp0dist
if not exist "!DEPS!" mkdir "!DEPS!"
echo.
echo  Building tunnel.dll from source...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0get-wireguard-dlls.ps1" -Deps "!DEPS!" -Dist "!DIST!"
if errorlevel 1 ( echo. & echo  ERROR: DLL build failed. & goto dll_warn )
if not exist "!DIST!\wireguard.dll" ( echo  ERROR: wireguard.dll missing. & goto dll_warn )
if not exist "!DIST!\tunnel.dll"    ( echo  ERROR: tunnel.dll missing.    & goto dll_warn )
set DO_COPY_DLLS=1
goto dll_done

rem ── Option 3: download pre-built DLLs ───────────────────────────────────────
:do_dl
set DIST=%~dp0dist
echo.
echo  Downloading DLLs from GitHub...
echo.
set REPO=https://raw.githubusercontent.com/masselink/MasselGUARD/e44f8ea672657bc23497d298742327b74ad84ec6/wireguard-deps
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try {" ^
  "  Write-Host '  Downloading wireguard.dll...'; Invoke-WebRequest '%REPO%/wireguard.dll' -OutFile '%DIST%\wireguard.dll' -UseBasicParsing;" ^
  "  Write-Host '  Downloading tunnel.dll...';    Invoke-WebRequest '%REPO%/tunnel.dll'    -OutFile '%DIST%\tunnel.dll'    -UseBasicParsing;" ^
  "  Write-Host '  Done.'" ^
  "} catch { Write-Host ('ERROR: ' + $_.Exception.Message); exit 1 }"
if errorlevel 1 goto dll_warn
if not exist "!DIST!\wireguard.dll" goto dll_warn
if not exist "!DIST!\tunnel.dll"    goto dll_warn
echo.
echo  DLLs downloaded successfully.
set DO_COPY_DLLS=1
goto dll_done

:dll_warn
echo.
echo  WARNING: DLLs not available.
echo  Place tunnel.dll + wireguard.dll next to MasselGUARD.exe manually.
echo  (Companion mode works without them; Standalone mode requires them.)

:dll_done
echo.
echo  ==========================================
echo   BUILD SUCCESSFUL
echo  ==========================================
echo.
echo   dist\MasselGUARD.exe
echo   dist\lang\
echo   dist\theme\
if "!DO_COPY_DLLS!"=="1" (
    echo   dist\wireguard.dll
    echo   dist\tunnel.dll
    echo.
    echo   Standalone local tunnels: ready.
) else (
    echo.
    echo   NOTE: DLLs not included. For Standalone mode,
    echo   place tunnel.dll + wireguard.dll next to the exe.
)
echo.
echo   Target machine requires .NET 10 Desktop Runtime:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
pause
exit /b 0
