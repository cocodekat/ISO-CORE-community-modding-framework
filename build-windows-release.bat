# Builds the Windows release zip: dist\windows\ICCMF-<version>-windows.zip
#
# The release contains only the CLI (plus the SDK it uses to create new projects).
# Example projects are NOT bundled or built.
#
# Usage:
#   .\build-windows-release.ps1
#   .\build-windows-release.ps1 -OutputDirectory C:\some\dir
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "dist\windows")
)

$ErrorActionPreference = "Stop"
$Version = "0.5.2"
$Root = $PSScriptRoot

# ---- Find dotnet ----
if ($env:DOTNET_BIN) {
    $Dotnet = $env:DOTNET_BIN
} elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $Dotnet = (Get-Command dotnet).Source
} elseif (Test-Path (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")) {
    $Dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
} else {
    throw "Could not find the .NET SDK. Install .NET 8 or set DOTNET_BIN."
}
Write-Host "Using .NET: $Dotnet"

# ---- Pack the CLI ----
& $Dotnet pack (Join-Path $Root "cli\ICCMF.Cli.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "ICCMF CLI packaging failed." }

$Nupkg = Join-Path $Root "dist\ICCMF.Cli.$Version.nupkg"
if (-not (Test-Path $Nupkg)) { throw "Expected package not found: $Nupkg" }

# Sanity check: is the SDK packed inside the CLI package? (A .nupkg is a zip file.)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Archive = [System.IO.Compression.ZipFile]::OpenRead($Nupkg)
$HasSound = $false
try {
    $Entry = $Archive.Entries | Where-Object { $_.FullName -like "tools/*/any/ICCMF.Cli.dll" } | Select-Object -First 1
    if ($Entry) {
        $Reader = $Entry.Open()
        try {
            $Memory = New-Object System.IO.MemoryStream
            $Reader.CopyTo($Memory)
            $HasSound = [System.Text.Encoding]::ASCII.GetString($Memory.ToArray()).Contains('ICCMF.Sdk.SoundApi.cs')
        } finally { $Reader.Dispose() }
    }
} finally {
    $Archive.Dispose()
}
if ($HasSound) {
    Write-Host "Check: SDK (including SoundApi.cs) is embedded in the CLI."
} else {
    Write-Warning "SoundApi.cs is not embedded in the CLI package. Add it to cli\ICCMF.Cli.csproj (EmbeddedResource) and to ExtractSdk in cli\Program.cs."
}

# ---- Stage the release folder ----
$Stage = Join-Path $OutputDirectory "ICCMF-$Version-windows"
$Zip = Join-Path $OutputDirectory "ICCMF-$Version-windows.zip"
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
if (Test-Path $Zip) { Remove-Item $Zip -Force }
New-Item $Stage -ItemType Directory -Force | Out-Null

Copy-Item (Join-Path $Root "install-cli.bat") $Stage
Copy-Item (Join-Path $Root "build-windows-release.ps1") $Stage
Copy-Item (Join-Path $Root "README.md") $Stage
$Guide = Join-Path $Root "WINDOWS-GUIDE.md"
if (Test-Path $Guide) { Copy-Item $Guide $Stage }

# install-cli.bat looks for the package in <folder>\dist first, so ship it there.
$StageDist = Join-Path $Stage "dist"
New-Item $StageDist -ItemType Directory -Force | Out-Null
Copy-Item $Nupkg $StageDist

# Master SDK: the CLI uses it to set up new projects.
Copy-Item (Join-Path $Root "Sdk") (Join-Path $Stage "Sdk") -Recurse
Get-ChildItem (Join-Path $Stage "Sdk") -Force -Recurse -Filter ".DS_Store" -ErrorAction SilentlyContinue |
    Remove-Item -Force

# CLI source, without build output.
$CliRoot = Join-Path $Root "cli"
$StageCli = Join-Path $Stage "cli"
New-Item $StageCli -ItemType Directory -Force | Out-Null
Get-ChildItem $CliRoot -File -Recurse -Force | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -ne '.DS_Store'
} | ForEach-Object {
    $Relative = $_.FullName.Substring($CliRoot.Length).TrimStart([char[]]"\/")
    $Target = Join-Path $StageCli $Relative
    New-Item (Split-Path $Target) -ItemType Directory -Force | Out-Null
    Copy-Item $_.FullName $Target
}

# ---- Zip it ----
Compress-Archive -Path $Stage -DestinationPath $Zip -CompressionLevel Optimal
Write-Host "Windows release created: $Zip"