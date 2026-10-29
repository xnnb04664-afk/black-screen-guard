$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
$exe = Join-Path $dist '黑屏守护.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw '请先运行 build.ps1 并完成验证。' }
$version = [Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString(3)
$archive = Join-Path $dist "BlackScreenGuard-v$version-win.zip"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用说明.md') -Destination (Join-Path $dist '使用说明.md') -Force
Compress-Archive -LiteralPath @($exe, ($exe + '.config'), (Join-Path $dist '使用说明.md')) -DestinationPath $archive -Force
$checksums = @($archive, $exe) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_)
}
[IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $archive,(Join-Path $dist 'SHA256SUMS.txt') | Select-Object FullName,Length
