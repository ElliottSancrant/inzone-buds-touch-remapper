param(
    [switch]$NoStart,
    [ValidateNotNullOrEmpty()][string]$DeskName = 'Yeti',
    [ValidateNotNullOrEmpty()][string]$BudsName = 'INZONE Buds',
    [string]$DeskId,
    [string]$BudsId,
    [string]$Config,
    [ValidateRange(250, 10000)][int]$WindowMs = 4000,
    [ValidateSet('ambient-triple', 'volume-return', 'mute-state')][string]$Gesture = 'ambient-triple'
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
$cliArguments = [System.Collections.Generic.List[string]]::new()
$cliArguments.Add('--background'); $cliArguments.Add('--enable-actions')
if ($Config) {
    if ($PSBoundParameters.ContainsKey('Gesture')) { throw 'Use bindings in the config instead of -Gesture.' }
    $cliArguments.Add('--config'); $cliArguments.Add((Resolve-Path -LiteralPath $Config).Path)
}
foreach ($option in @(
    @{ Parameter = 'DeskName'; Flag = '--desk-name'; Value = $DeskName },
    @{ Parameter = 'BudsName'; Flag = '--buds-name'; Value = $BudsName },
    @{ Parameter = 'WindowMs'; Flag = '--window-ms'; Value = $WindowMs },
    @{ Parameter = 'Gesture'; Flag = '--trigger'; Value = $Gesture }
)) {
    if (!$Config -or $PSBoundParameters.ContainsKey($option.Parameter)) {
        $cliArguments.Add($option.Flag); $cliArguments.Add([string]$option.Value)
    }
}
if ($DeskId) { $cliArguments.Add('--desk-id'); $cliArguments.Add($DeskId) }
if ($BudsId) { $cliArguments.Add('--buds-id'); $cliArguments.Add($BudsId) }
# Validate before touching an existing installation. The config stays at its original path.
$consoleExe = Join-Path $PSScriptRoot 'dist/InzoneMicSwitch.exe'
if (!(Test-Path -LiteralPath $consoleExe)) { throw 'The release console executable is required to validate configuration.' }
& $consoleExe --validate-config @cliArguments
if ($LASTEXITCODE -ne 0) { throw 'Configuration validation failed; installation was left unchanged.' }
$arguments = ($cliArguments | ForEach-Object { Quote-Argument $_ }) -join ' '
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
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Run configured INZONE Buds gesture actions. Default: switch Windows microphones. Windowless, current user only.' -Force | Out-Null
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
