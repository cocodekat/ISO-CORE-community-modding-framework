@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "VERSION=0.5.2"

if defined DOTNET_BIN goto dotnet_found
where dotnet >nul 2>nul
if not errorlevel 1 (
  set "DOTNET_BIN=dotnet"
  goto dotnet_found
)
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
  set "DOTNET_BIN=%USERPROFILE%\.dotnet\dotnet.exe"
  goto dotnet_found
)

echo Could not find the .NET SDK. Install .NET 8, then run this file again. 1>&2
exit /b 127

:dotnet_found
set "PACKAGE_SOURCE=%SCRIPT_DIR%dist"
if not exist "%PACKAGE_SOURCE%\ICCMF.Cli.%VERSION%.nupkg" set "PACKAGE_SOURCE=%SCRIPT_DIR%"
if not exist "%PACKAGE_SOURCE%\ICCMF.Cli.%VERSION%.nupkg" (
  echo ICCMF.Cli.%VERSION%.nupkg was not found next to this installer or in dist. 1>&2
  exit /b 2
)

"%DOTNET_BIN%" tool update --global ICCMF.Cli --add-source "%PACKAGE_SOURCE%" --version %VERSION% --ignore-failed-sources
if errorlevel 1 (
  "%DOTNET_BIN%" tool install --global ICCMF.Cli --add-source "%PACKAGE_SOURCE%" --version %VERSION% --ignore-failed-sources
  if errorlevel 1 exit /b %errorlevel%
)

echo Installed iccmf %VERSION%.
echo If Windows cannot find iccmf, add %%USERPROFILE%%\.dotnet\tools to your user PATH and open a new terminal.

