param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $repoRoot 'build'
$distRoot = Join-Path $repoRoot 'dist'
& (Join-Path $PSScriptRoot 'build.ps1')
if (!$InnoCompiler) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'))
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$InnoCompiler -or !(Test-Path -LiteralPath $InnoCompiler)) { throw 'Install Inno Setup 6 or 7, or pass -InnoCompiler C:\path\ISCC.exe.' }
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
& $InnoCompiler (Join-Path $repoRoot 'installer\VolumeDock.iss')
if ($LASTEXITCODE -ne 0) { throw 'VolumeDock installer compilation failed.' }
$portablePath = Join-Path $distRoot 'VolumeDock-1.0.0-Portable-x64.zip'
Compress-Archive -Path (Join-Path $buildRoot '*') -DestinationPath $portablePath -Force
$assets = @((Join-Path $distRoot 'VolumeDock-1.0.0-Setup-x64.exe'),$portablePath)
$checksumLines = foreach ($asset in $assets) {
    $checksum = Get-FileHash -LiteralPath $asset -Algorithm SHA256
    $checksum.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($asset)
}
[IO.File]::WriteAllLines((Join-Path $distRoot 'SHA256SUMS.txt'), [string[]]$checksumLines, (New-Object Text.UTF8Encoding($false)))
Write-Output ('Release assets: ' + $distRoot)
