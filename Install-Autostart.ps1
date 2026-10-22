param(
    [switch]$NoStart,
    [ValidateNotNullOrEmpty()][string]$DeskName = 'Yeti',
    [ValidateNotNullOrEmpty()][string]$BudsName = 'INZONE Buds',
    [string]$DeskId,
    [string]$BudsId,
    [ValidateRange(250, 10000)][int]$WindowMs = 4000,
    [ValidateSet('volume-return', 'mute-state')][string]$Gesture = 'volume-return'
)
$ErrorActionPreference = 'Stop'
$taskName = 'INZONE Microphone Switcher'
$installDir = Join-Path $env:LOCALAPPDATA 'InzoneMicSwitch/app'
$sourceDir = Join-Path $PSScriptRoot 'dist-background'
$exe = Join-Path $installDir 'InzoneMicSwitch.exe'
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
if (!(Test-Path -LiteralPath (Join-Path $sourceDir 'InzoneMicSwitch.exe'))) { throw 'Extract a release ZIP, or run Build.ps1 and use its package folder before installing.' }
# Quote each argument using Windows command-line escaping (including trailing backslashes).
function Quote-Argument([string]$Value) {
    '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}
$arguments = '--background --enable-switch --desk-name ' + (Quote-Argument $DeskName) +
    ' --buds-name ' + (Quote-Argument $BudsName) + ' --window-ms ' + $WindowMs + ' --trigger ' + $Gesture
if ($DeskId) { $arguments += ' --desk-id ' + (Quote-Argument $DeskId) }
if ($BudsId) { $arguments += ' --buds-id ' + (Quote-Argument $BudsId) }
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Actions.Execute -ne $exe) { throw 'An unrelated task uses this name; it was left unchanged.' }
    Stop-ScheduledTask -TaskName $taskName
}
# Stop only this installation when updating its files.
Get-CimInstance Win32_Process | Where-Object ExecutablePath -eq $exe | ForEach-Object {
    $process = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
    if ($process) { Stop-Process -Id $process.Id; $process.WaitForExit(5000) | Out-Null }
}
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Get-ChildItem -LiteralPath $sourceDir -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $installDir $_.Name) -Force }
$action = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $installDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
$principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Switch Windows default microphones using INZONE Buds touch controls. Windowless, current user only.' -Force | Out-Null
if (!$NoStart) {
    # Replace the manually started workspace copy, if it is still running.
    $workspaceExe = Join-Path $PSScriptRoot 'dist/InzoneMicSwitch.exe'
    Get-CimInstance Win32_Process | Where-Object ExecutablePath -eq $workspaceExe | ForEach-Object {
        $process = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
        if ($process) { Stop-Process -Id $process.Id; $process.WaitForExit(5000) | Out-Null }
    }
    Start-ScheduledTask -TaskName $taskName
}
Write-Output "Installed: $exe"
Write-Output "At sign-in task: $taskName"
Write-Output "Log: $(Join-Path $env:LOCALAPPDATA 'InzoneMicSwitch/logs/bridge.log')"
