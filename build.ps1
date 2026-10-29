param([string]$OutputPath, [switch]$TestBuild)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows 的 .NET Framework 4.8。' }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Where-Object { $TestBuild -or $_.Name -ne 'SelfTests.cs' } | ForEach-Object FullName)
$defaultOutput = if ($TestBuild) { '.artifacts/test/BlackScreenGuard.Tests.exe' } else { 'dist/黑屏守护.exe' }
$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $PSScriptRoot $defaultOutput }
[void][IO.Directory]::CreateDirectory((Split-Path $output -Parent))
$defines = if ($TestBuild) { '/define:TEST_BUILD' } else { '/define:RELEASE_BUILD' }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /warnaserror+ $defines "/out:$output" "/win32icon:$PSScriptRoot/assets/app.ico" "/win32manifest:$PSScriptRoot/src/app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Security.dll @sources
if ($LASTEXITCODE -ne 0) { throw "编译失败：$LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/app.config') -Destination ($output + '.config') -Force
Get-Item -LiteralPath $output | Select-Object FullName, Length, LastWriteTime
