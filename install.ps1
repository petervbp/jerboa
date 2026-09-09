<#
    Installs Jerboa for the current user:
      - copies the published executable to %LOCALAPPDATA%\Programs\Jerboa
      - puts a shortcut in the Start menu
      - registers it to start at logon, minimised to the notification area

    Nothing is written outside the current user's profile, and no administrator
    rights are needed. Run uninstall.ps1 to undo all three steps.
#>
[CmdletBinding()]
param(
    [string] $Source = (Join-Path $PSScriptRoot 'dist\Jerboa.exe'),
    [switch] $NoAutostart,
    [switch] $NoShortcut
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) {
    throw "Jerboa.exe not found at $Source. Run: dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist"
}

$targetDir = Join-Path $env:LOCALAPPDATA 'Programs\Jerboa'
$targetExe = Join-Path $targetDir 'Jerboa.exe'

# A running copy holds a lock on its own file, so ask it to go away first.
$running = Get-Process -Name 'Jerboa' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Closing the running copy of Jerboa…'
    $running | Stop-Process
    Start-Sleep -Milliseconds 600
}

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item $Source $targetExe -Force
Write-Host "Installed  $targetExe"

if (-not $NoShortcut) {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $linkPath = Join-Path $startMenu 'Jerboa.lnk'

    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $targetExe
    $link.WorkingDirectory = $targetDir
    $link.Description = 'Record system audio and microphone into one stereo MP3'
    $link.Save()
    Write-Host "Shortcut   $linkPath"
}

# Autostart is registered twice: a shortcut in the Startup folder, which you can see and
# delete, and a Run registry value as a backstop. Windows has been known to ignore either
# one silently, and a duplicate launch is harmless — the second copy finds the first
# already running and exits.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Jerboa.lnk'

if ($NoAutostart) {
    if (Test-Path $startupLink) { Remove-Item $startupLink }
    if (Get-ItemProperty -Path $runKey -Name 'Jerboa' -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name 'Jerboa'
    }
} else {
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'Jerboa' -Value "`"$targetExe`" --minimized"

    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($startupLink)
    $link.TargetPath = $targetExe
    $link.Arguments = '--minimized'
    $link.WorkingDirectory = $targetDir
    $link.Description = 'Record system audio and microphone into one stereo MP3'
    $link.Save()
    Write-Host "Autostart  $startupLink"
}

Write-Host ''
Write-Host 'Done. Jerboa is in the Start menu and will be there after the next sign-in.'
