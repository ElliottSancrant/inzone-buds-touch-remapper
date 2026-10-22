param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Build and self-tests require Windows.' }
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 9 SDK first. See README.md.' }
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
# A fresh staging directory prevents stale files entering a release. No existing files are deleted.
$stage = Join-Path $outputRoot ('build-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'InzoneMicSwitch'
$project = Join-Path $PSScriptRoot 'src/InzoneMicSwitch/InzoneMicSwitch.csproj'
New-Item -ItemType Directory -Path $package -Force | Out-Null
foreach ($variant in @(@{ Configuration = 'Release'; Folder = 'dist' }, @{ Configuration = 'Background'; Folder = 'dist-background' })) {
    & dotnet publish $project -c $variant.Configuration -r win-x64 --self-contained false `
        --artifacts-path (Join-Path $stage 'intermediate') -o (Join-Path $package $variant.Folder) `
        -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($variant.Configuration)" }
    $appBytes = [System.IO.File]::ReadAllBytes((Join-Path $package ($variant.Folder + '/InzoneMicSwitch.exe')))
    $peOffset = [BitConverter]::ToInt32($appBytes, 0x3c)
    $subsystem = [BitConverter]::ToUInt16($appBytes, $peOffset + 24 + 68)
    $expectedSubsystem = if ($variant.Configuration -eq 'Background') { 2 } else { 3 }
    if ($subsystem -ne $expectedSubsystem) { throw "Wrong executable subsystem for $($variant.Configuration): $subsystem" }
}
& (Join-Path $package 'dist/InzoneMicSwitch.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Offline self-tests failed.' }
foreach ($file in @('README.md', 'CONTRIBUTING.md', 'LICENSE', 'NOTICE', 'Install-Autostart.ps1', 'Disable-Autostart.ps1',
    'Enable-Autostart.cmd', 'Disable-Autostart.cmd', 'Start-MicSwitch.cmd', 'Preview-MicSwitch.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $package
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $package -Recurse
$archive = Join-Path $outputRoot 'InzoneMicSwitch-win-x64.zip'
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Encoding ASCII -Value "$hash  InzoneMicSwitch-win-x64.zip"
Write-Output "Package folder: $package"
Write-Output "Release ZIP: $archive"
Write-Output "SHA256: $hash"
