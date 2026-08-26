@echo off
setlocal enabledelayedexpansion
title StreamBox Build Script
cd /d "%~dp0"

echo ============================================================
echo  StreamBox Build Script
echo ============================================================
echo.

rem ── [1/9] Check .NET SDK ──
echo [1/9] Checking .NET SDK...
dotnet --version >nul 2>&1
if !errorlevel! neq 0 (
    echo ERROR: .NET SDK not found.
    echo   Install from https://dotnet.microsoft.com/download
    goto :fail
)
for /f "delims=" %%v in ('dotnet --version') do set "DOTNET_VER=%%v"
echo   Found .NET SDK !DOTNET_VER!
echo.

rem ── [2/9] Check Inno Setup ──
echo [2/9] Checking Inno Setup...
set "ISCC="
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if "!ISCC!"=="" (
    echo ERROR: Inno Setup 6 not found.
    echo   Install from https://jrsoftware.org/isinfo.php
    goto :fail
)
echo   Found: !ISCC!
echo.

rem ── [3/9] Check ImageMagick ──
echo [3/9] Checking ImageMagick...
where magick >nul 2>&1
if !errorlevel! neq 0 (
    echo ERROR: ImageMagick not found on PATH.
    echo   Install from https://imagemagick.org/script/download.php
    echo   After installing, make sure "magick" is available in your PATH.
    goto :fail
)
for /f "tokens=2" %%v in ('magick --version 2^>nul ^| findstr /i "Version"') do set "IM_VER=%%v"
echo   Found ImageMagick !IM_VER!
echo.

rem ── [4/9] Generate app-icon.ico from logo.png ──
echo [4/9] Generating app-icon.ico from Assets\logo.png...
if not exist "Assets\logo.png" (
    echo ERROR: Assets\logo.png not found.
    goto :fail
)
magick "Assets\logo.png" -define icon:auto-resize=256,128,64,48,32,16 "Assets\app-icon.ico"
if !errorlevel! neq 0 (
    echo ERROR: ImageMagick icon conversion failed.
    goto :fail
)
if not exist "Assets\app-icon.ico" (
    echo ERROR: Assets\app-icon.ico was not created.
    goto :fail
)
echo   Generated: Assets\app-icon.ico
echo.

rem ── [5/9] Clean old build artifacts ──
echo [5/9] Cleaning old build artifacts...
if exist "bin" rmdir /s /q "bin" 2>nul
if exist "obj" rmdir /s /q "obj" 2>nul
if exist "Output" rmdir /s /q "Output" 2>nul
echo   Done.
echo.

rem ── [6/9] dotnet restore ──
echo [6/9] Restoring NuGet packages...
call dotnet restore StreamBox.csproj
if !errorlevel! neq 0 (
    echo ERROR: dotnet restore failed.
    goto :fail
)
echo.

rem ── [7/9] dotnet publish ──
echo [7/9] Publishing ^(self-contained, single-file, win-x64^)...
call dotnet publish StreamBox.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o bin\Release\net8.0\win-x64\publish
if !errorlevel! neq 0 (
    echo ERROR: dotnet publish failed.
    goto :fail
)
echo.

rem ── [8/9] Verify required files in publish output ──
echo [8/9] Verifying publish output...
set "PUBDIR=bin\Release\net8.0\win-x64\publish"

if not exist "!PUBDIR!\StreamBox.exe" (
    echo ERROR: StreamBox.exe not found in publish output.
    goto :fail
)
echo   StreamBox.exe found.

if not exist "!PUBDIR!\Assets\app-icon.ico" (
    echo WARNING: Assets\app-icon.ico not in publish output ^(window icon may not display^).
)

set "MPV_FOUND=0"
if exist "!PUBDIR!\mpv\win-x64\libmpv-2.dll" set "MPV_FOUND=1"
if exist "!PUBDIR!\mpv\win-x64\mpv-2.dll" set "MPV_FOUND=1"
if exist "!PUBDIR!\mpv\win-x64\mpv-1.dll" set "MPV_FOUND=1"
if "!MPV_FOUND!"=="0" (
    echo ERROR: libmpv DLL not found in !PUBDIR!\mpv\win-x64\
    echo   Expected one of: libmpv-2.dll / mpv-2.dll / mpv-1.dll
    echo   Place the 64-bit libmpv build under mpv\win-x64\ before building.
    goto :fail
)
echo   libmpv native DLL found.
echo.

rem ── [9/9] Build installer with Inno Setup ──
echo [9/9] Building installer...
"!ISCC!" StreamBox.iss
if !errorlevel! neq 0 (
    echo ERROR: Inno Setup compilation failed.
    goto :fail
)
echo.

echo ============================================================
echo  BUILD SUCCESSFUL
echo  Installer: Output\StreamBox-Setup.exe
echo ============================================================
goto :done

:fail
echo.
echo ============================================================
echo  BUILD FAILED - see errors above.
echo ============================================================

:done
echo.
echo Press any key to exit...
pause >nul
endlocal
