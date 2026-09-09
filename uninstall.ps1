<#
    Removes everything install.ps1 created. Recordings and the settings file are
    left alone; delete %APPDATA%\Jerboa by hand if you want those gone too.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Get-Process -Name 'Jerboa' -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 400

$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Jerboa.lnk'
if (Test-Path $startupLink) { Remove-Item $startupLink; Write-Host 'Autostart  removed' }

# The Run entry earlier versions used, in case this is an upgrade from one of those.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'Jerboa' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'Jerboa'
}

$link = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Jerboa.lnk'
if (Test-Path $link) { Remove-Item $link; Write-Host "Shortcut   removed" }

$targetDir = Join-Path $env:LOCALAPPDATA 'Programs\Jerboa'
if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force; Write-Host "Program    removed" }

Write-Host ''
Write-Host 'Jerboa is uninstalled. Settings remain in %APPDATA%\Jerboa.'
