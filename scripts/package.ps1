param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $repoRoot 'build'
$distRoot = Join-Path $repoRoot 'dist'
$version = '1.0.1'
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
$portablePath = Join-Path $distRoot "VolumeDock-$version-Portable-x64.zip"
Compress-Archive -Path (Join-Path $buildRoot '*') -DestinationPath $portablePath -Force
$sourcePath = Join-Path $distRoot "VolumeDock-$version-Source.zip"
$sourceStage = Join-Path ([IO.Path]::GetTempPath()) "VolumeDock-$version-source-$([guid]::NewGuid().ToString('N'))"
$sourceProject = Join-Path $sourceStage 'VolumeDock'
New-Item -ItemType Directory -Path $sourceProject -Force | Out-Null
try {
    foreach ($name in @('assets','docs','installer','licenses','scripts','src')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $sourceProject -Recurse
    }
    foreach ($name in @('.gitattributes','.gitignore','LICENSE','README.md','THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $sourceProject
    }
    Compress-Archive -Path $sourceProject -DestinationPath $sourcePath -Force
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($sourceStage)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedStage.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
$assets = @((Join-Path $distRoot "VolumeDock-$version-Setup-x64.exe"),$portablePath,$sourcePath)
$checksumLines = foreach ($asset in $assets) {
    $checksum = Get-FileHash -LiteralPath $asset -Algorithm SHA256
    $checksum.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($asset)
}
[IO.File]::WriteAllLines((Join-Path $distRoot 'SHA256SUMS.txt'), [string[]]$checksumLines, (New-Object Text.UTF8Encoding($false)))
Write-Output ('Release assets: ' + $distRoot)
