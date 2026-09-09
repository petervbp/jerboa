<#
    Builds Jerboa and installs it for the current user in one go.

    Open PowerShell in this folder and run:

        powershell -ExecutionPolicy Bypass -File setup.ps1

    You need the .NET 8 SDK first — https://dotnet.microsoft.com/download/dotnet/8.0
    Nothing is installed outside your own user profile and no administrator
    rights are needed. Run uninstall.ps1 to remove everything again.
#>
[CmdletBinding()]
param(
    [switch] $NoAutostart,
    [switch] $NoShortcut
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ''
    Write-Host 'The .NET SDK is not installed.' -ForegroundColor Yellow
    Write-Host 'Get it from https://dotnet.microsoft.com/download/dotnet/8.0 (the SDK, not the runtime),'
    Write-Host 'then close this window, open a new one and run setup.ps1 again.'
    Write-Host ''
    exit 1
}

Write-Host 'Building Jerboa. The first run downloads some pieces and takes a minute or two…'
Write-Host ''

# win-x64 on purpose: the MP3 encoder ships as an x86/x64 library only. On an ARM
# machine this still builds and runs, through Windows' x64 emulation.
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o dist

if ($LASTEXITCODE -ne 0) { throw 'The build failed. The messages above say why.' }

Write-Host ''
& (Join-Path $PSScriptRoot 'install.ps1') -NoAutostart:$NoAutostart -NoShortcut:$NoShortcut
