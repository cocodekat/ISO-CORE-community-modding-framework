# ISO-CORE modding setup for Windows.
# Installs BepInEx 5 next to the game executable and installs the ICCMF command-line tool.
#
# Usage:  powershell -ExecutionPolicy Bypass -File .\install.ps1 [-Force]
#   -Force   reinstall BepInEx even if it is already installed
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes Invoke-WebRequest much faster

$BepInExVersion = '5.4.23.6'
$IccmfVersion = '0.5.2'
$Repo = 'cocodekat/ISO-CORE-community-modding-framework'
$IccmfUrl = "https://github.com/$Repo/raw/main/dist/windows/ICCMF-$IccmfVersion-windows.zip"

function Fail([string]$Message) {
    Write-Host "Error: $Message" -ForegroundColor Red
    exit 1
}

# ---- 1. Detect the operating system ----
$onWindows = ($PSVersionTable.PSVersion.Major -lt 6) -or $IsWindows
if (-not $onWindows) {
    if ($IsMacOS) { Fail "This is the Windows installer. On macOS, run: sh install.sh" }
    Fail "Unsupported operating system. Only Windows and macOS are supported."
}
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("iccmf-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    # ---- 2. Ask where ISO-CORE is ----
    $inputPath = Read-Host "Path to the ISO-CORE executable (for example C:\...\ISO-CORE Playtest\ISO CORE.exe)"
    $inputPath = ($inputPath -replace '^\s*["'']|["'']\s*$', '').Trim()
    if (-not $inputPath) { Fail "No location given. Nothing was installed." }

    $exe = $null
    if (Test-Path -LiteralPath $inputPath -PathType Leaf) {
        $exe = Get-Item -LiteralPath $inputPath
    } elseif (Test-Path -LiteralPath $inputPath -PathType Container) {
        $exe = Get-ChildItem -LiteralPath $inputPath -Filter '*.exe' -File |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.DirectoryName ($_.BaseName + '_Data')) } |
            Select-Object -First 1
    }
    if (-not $exe -or $exe.Extension -ne '.exe') { Fail "Could not find the ISO-CORE executable at: $inputPath" }

    $gameDir = $exe.DirectoryName
    $dataDir = Join-Path $gameDir ($exe.BaseName + '_Data')
    if (-not (Test-Path -LiteralPath (Join-Path $dataDir 'Managed\Assembly-CSharp.dll'))) {
        Fail "$($exe.Name) does not look like a Unity game: $dataDir\Managed\Assembly-CSharp.dll is missing."
    }
    Write-Host "Found ISO-CORE: $($exe.FullName)"

    # BepInEx must match the game's architecture; read it from the exe header.
    $arch = 'x64'
    try {
        $stream = [IO.File]::OpenRead($exe.FullName)
        try {
            $buffer = New-Object byte[] 4096
            [void]$stream.Read($buffer, 0, $buffer.Length)
        } finally { $stream.Dispose() }
        $machine = [BitConverter]::ToUInt16($buffer, [BitConverter]::ToInt32($buffer, 0x3C) + 4)
        if ($machine -eq 0x014c) { $arch = 'x86' }
        elseif ($machine -ne 0x8664) { Fail ("Unsupported executable architecture (0x{0:x4})." -f $machine) }
    } catch {
        Write-Host "Could not read the executable's architecture; assuming 64-bit."
    }
    Write-Host "Game architecture: $arch"

    # ---- .NET SDK (needed for ICCMF); check now so nothing is half-installed ----
    $dotnet = $env:DOTNET_BIN
    if (-not $dotnet) {
        $found = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($found) { $dotnet = $found.Source }
    }
    if (-not $dotnet -and (Test-Path (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'))) {
        $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    }
    if (-not $dotnet) {
        Fail ".NET SDK not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download and run this again."
    }
    if (-not (& $dotnet --list-sdks)) {
        Fail "Found dotnet at $dotnet, but no .NET SDK is installed (only a runtime). Install the .NET 8 SDK."
    }
    $env:DOTNET_BIN = $dotnet

    # ---- 3. Install BepInEx ----
    $bepCore = Join-Path $gameDir 'BepInEx\core\BepInEx.dll'
    if ((Test-Path -LiteralPath $bepCore) -and -not $Force) {
        Write-Host "BepInEx is already installed in $gameDir (use -Force to reinstall). Skipping."
    } else {
        Write-Host "Finding BepInEx $BepInExVersion for Windows ($arch)..."
        $url = $null
        try {
            $release = Invoke-RestMethod -Headers @{ 'User-Agent' = 'iccmf-installer' } `
                -Uri "https://api.github.com/repos/BepInEx/BepInEx/releases/tags/v$BepInExVersion"
            $asset = $release.assets | Where-Object { $_.name -like "BepInEx_win_${arch}_*.zip" } | Select-Object -First 1
            if ($asset) { $url = $asset.browser_download_url }
        } catch { }
        if (-not $url) {
            $url = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_${arch}_$BepInExVersion.zip"
        }

        Write-Host "Downloading $url"
        $zip = Join-Path $tmp 'bepinex.zip'
        try { Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing }
        catch { Fail "Could not download BepInEx from $url" }
        Expand-Archive -LiteralPath $zip -DestinationPath $gameDir -Force

        if (-not (Test-Path -LiteralPath $bepCore)) { Fail "BepInEx did not extract correctly into $gameDir." }
        Write-Host "BepInEx $BepInExVersion installed."
    }

    # ---- 4. Install ICCMF ----
    Write-Host "Downloading ICCMF $IccmfVersion..."
    $iccmfZip = Join-Path $tmp 'iccmf.zip'
    try { Invoke-WebRequest -Uri $IccmfUrl -OutFile $iccmfZip -UseBasicParsing }
    catch { Fail "Could not download ICCMF from $IccmfUrl" }
    $iccmfDir = Join-Path $tmp 'iccmf'
    Expand-Archive -LiteralPath $iccmfZip -DestinationPath $iccmfDir -Force
    $installer = Get-ChildItem -LiteralPath $iccmfDir -Filter 'install-cli.bat' -File -Recurse | Select-Object -First 1
    if (-not $installer) { Fail "install-cli.bat was not found inside the ICCMF download." }

    Push-Location $installer.DirectoryName
    try {
        & cmd.exe /c 'install-cli.bat'
        if ($LASTEXITCODE -ne 0) { Fail "The ICCMF installer failed." }
    } finally { Pop-Location }

    # Tell iccmf where the game is (user-level environment variable).
    [Environment]::SetEnvironmentVariable('ISOCORE_GAME_DIR', $gameDir, 'User')
    $env:ISOCORE_GAME_DIR = $gameDir

    # ---- 5. Done ----
    Write-Host ""
    Write-Host "============================================================"
    Write-Host " Done"
    Write-Host "============================================================"
    Write-Host "BepInEx and ICCMF are installed. No Steam launch option is needed on Windows:"
    Write-Host "just start the game from Steam. After the first launch,"
    Write-Host "$gameDir\BepInEx\LogOutput.log should show BepInEx starting."
    Write-Host ""
    Write-Host "Set ISOCORE_GAME_DIR to: $gameDir"
    Write-Host "Open a NEW terminal before using iccmf so it picks that up."
    if (-not (Get-Command iccmf -ErrorAction SilentlyContinue)) {
        Write-Host "If 'iccmf' is not found, add %USERPROFILE%\.dotnet\tools to your user PATH."
    }
    Write-Host ""
    Write-Host "Create your first mod with:  iccmf create mod MyMod"
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}