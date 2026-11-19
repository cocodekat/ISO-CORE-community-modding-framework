@echo off
setlocal
set "SCRIPT_DIR=%~dp0"

if defined DOTNET_BIN goto build
where dotnet >nul 2>nul
if not errorlevel 1 (
  set "DOTNET_BIN=dotnet"
  goto build
)
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
  set "DOTNET_BIN=%USERPROFILE%\.dotnet\dotnet.exe"
  goto build
)

echo Could not find the .NET SDK. Install .NET 8 and try again. 1>&2
exit /b 127

:build
"%DOTNET_BIN%" build "%SCRIPT_DIR%ZombieSurvival.csproj" -c Release %*

