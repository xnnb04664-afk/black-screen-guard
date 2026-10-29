$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $project 'dist/黑屏守护.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw '请先运行 build.ps1 编译正式版。' }
& (Join-Path $project 'build.ps1') -TestBuild
$testExe = Join-Path $project '.artifacts/test/BlackScreenGuard.Tests.exe'
$report = Join-Path $project ('.artifacts/release-check/' + [Guid]::NewGuid().ToString('N'))
$process = Start-Process -FilePath $testExe -ArgumentList @('--verify-release', ('"' + $exe + '"'), ('"' + $report + '"')) -PassThru -WindowStyle Hidden
try {
    if (-not $process.WaitForExit(30000)) { throw '正式版验证超时。' }
    Get-Content -LiteralPath (Join-Path $report 'release-tests.txt')
    if ($process.ExitCode -ne 0) { throw "正式版验证失败：$($process.ExitCode)" }
    Write-Output "验证结果：$report"
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
    $process.Dispose()
}
