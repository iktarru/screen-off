# Сборка и установка ночного режима экрана.
#   - компилирует DisplayOff.exe в %USERPROFILE%\Tools
#   - кладёт скилл в %USERPROFILE%\.claude\skills\screen-off\
#   - создаёт на рабочем столе ярлык с горячей клавишей Ctrl+Alt+D
# Права администратора не нужны.

$ErrorActionPreference = 'Stop'

$repo      = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolsDir  = Join-Path $env:USERPROFILE 'Tools'
$skillDir  = Join-Path $env:USERPROFILE '.claude\skills\screen-off'
$exePath   = Join-Path $toolsDir 'DisplayOff.exe'
$srcPath   = Join-Path $repo 'src\DisplayOff.cs'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "Не найден компилятор C# ($csc). Нужен .NET Framework 4.x."
}

New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
New-Item -ItemType Directory -Force -Path $skillDir | Out-Null

Write-Host 'Собираю DisplayOff.exe...'
& $csc -nologo -target:winexe -optimize+ `
    -r:System.Management.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll `
    "-out:$exePath" $srcPath
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }

Write-Host 'Ставлю скилл для Claude Code...'
Copy-Item (Join-Path $repo 'SKILL.md') $skillDir -Force

Write-Host 'Создаю ярлык с горячей клавишей Ctrl+Alt+D...'
$desktop  = [Environment]::GetFolderPath('Desktop')
$lnkPath  = Join-Path $desktop 'Display Off.lnk'
$shell    = New-Object -ComObject WScript.Shell
$lnk      = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath       = $exePath
$lnk.WorkingDirectory = $toolsDir
$lnk.IconLocation     = 'imageres.dll,109'
$lnk.Description      = 'Ночной режим экрана (Ctrl+Alt+D)'
$lnk.Hotkey           = 'CTRL+ALT+D'
$lnk.Save()

Write-Host ''
Write-Host 'Готово:'
Write-Host "  программа : $exePath"
Write-Host "  скилл     : $skillDir\SKILL.md"
Write-Host "  ярлык     : $lnkPath  (Ctrl+Alt+D)"
Write-Host ''
Write-Host 'Ярлык должен оставаться на рабочем столе или в меню Пуск - иначе Windows'
Write-Host 'не обрабатывает его горячую клавишу. Команда /screen-off в Claude Code'
Write-Host 'появится при следующем запуске сессии.'
