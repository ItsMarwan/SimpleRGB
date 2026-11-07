# Builds SimpleRGB (Release) and installs it for the current user. No administrator rights needed.
#   powershell -ExecutionPolicy Bypass -File install.ps1
param([switch]$NoShortcuts)
$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$dist   = Join-Path $root 'dist\SimpleRGB'
$target = Join-Path $env:LOCALAPPDATA 'Programs\SimpleRGB'

Write-Host 'Building SimpleRGB...'
dotnet publish "$root\src\SimpleRGB\SimpleRGB.csproj" -c Release -r win-x64 --self-contained false -o $dist -p:DebugType=none -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Host "Installing to $target ..."
New-Item -ItemType Directory -Force $target | Out-Null
try {
    Copy-Item "$dist\*" $target -Recurse -Force
} catch {
    throw "Could not replace the installed files. If SimpleRGB is running, exit it from its tray icon (right-click > Exit) and run this again."
}

if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $exe = Join-Path $target 'SimpleRGB.exe'
    foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {
        $lnk = $shell.CreateShortcut((Join-Path $dir 'SimpleRGB.lnk'))
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $target
        $lnk.IconLocation = "$exe,0"
        $lnk.Description = 'SimpleRGB - simple RGB lighting control'
        $lnk.Save()
    }
}
# If "Start with Windows" is on, make sure it points at this installed copy (one admin prompt).
schtasks.exe /Query /TN SimpleRGB *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host 'Updating "Start with Windows" to use the installed copy (Windows will ask for permission)...'
    Start-Process (Join-Path $target 'SimpleRGB.exe') -ArgumentList '--register-autostart' -Verb RunAs -Wait
}
Write-Host "Done. Start SimpleRGB from the Start menu or the desktop shortcut."
