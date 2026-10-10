param([string]$OutputDirectory, [string]$RepositoryRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = if ($RepositoryRoot) { $RepositoryRoot } else { Split-Path -Parent $PSScriptRoot }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'build' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The x64 .NET Framework compiler is missing. Install .NET Framework 4.8 on Windows.' }
$srcRoot = Join-Path $repoRoot 'src'
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
$references += @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot ('WPF\' + $_)) }
$sources = @('AudioPanel.cs','AudioBackend.cs','TrayVolumeHook.cs','SpatialAudioState.cs','AssemblyInfo.cs') | ForEach-Object { Join-Path $srcRoot $_ }
$compilerArguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/langversion:5','/utf8output','/codepage:65001',
    ('/out:' + (Join-Path $OutputDirectory 'VolumeDock.exe')),
    ('/win32manifest:' + (Join-Path $srcRoot 'AudioPanel.manifest')),
    ('/win32icon:' + (Join-Path $repoRoot 'assets\VolumeDock.ico')),
    ('/resource:' + (Join-Path $srcRoot 'PanelResources.xaml') + ',AudioStyles')) + $references + $sources
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'VolumeDock compilation failed.' }
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses\EarTrumpet.txt') -Destination (Join-Path $OutputDirectory 'licenses\EarTrumpet.txt') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE'),(Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md'),(Join-Path $repoRoot 'README.md') -Destination $OutputDirectory -Force
Write-Output ('Built ' + (Join-Path $OutputDirectory 'VolumeDock.exe'))
