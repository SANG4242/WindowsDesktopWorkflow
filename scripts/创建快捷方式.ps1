$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $projectRoot 'app\DesktopWorkflow.exe'
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutName = ConvertFrom-Json '"\u684c\u9762\u5de5\u4f5c\u6d41.lnk"'
$shell = New-Object -ComObject WScript.Shell

foreach ($folder in @($desktop, $startMenu)) {
    $shortcutPath = Join-Path $folder $shortcutName
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $appPath
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = Split-Path -Parent $appPath
    $shortcut.IconLocation = "$appPath,0"
    $shortcut.Description = 'Desktop workflow control center'
    $shortcut.Save()
    Write-Output $shortcutPath
}