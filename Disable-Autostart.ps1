$ErrorActionPreference = 'Stop'
$taskName = 'INZONE Microphone Switcher'
$exe = Join-Path $env:LOCALAPPDATA 'InzoneMicSwitch/app/InzoneMicSwitch.exe'
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) {
    if ($task.Actions.Execute -ne $exe) { throw 'Unexpected task executable; left unchanged.' }
    Disable-ScheduledTask -TaskName $taskName | Out-Null
    Stop-ScheduledTask -TaskName $taskName
}
Get-CimInstance Win32_Process | Where-Object ExecutablePath -eq $exe | ForEach-Object { Stop-Process -Id $_.ProcessId }
Write-Output 'Automatic startup disabled and background bridge stopped. Your current microphone selection was preserved.'
