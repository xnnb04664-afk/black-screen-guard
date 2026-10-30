param([string]$Baseline)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
& (Join-Path $project 'build.ps1') -TestBuild
$helper = Join-Path $project '.artifacts/test/BlackScreenGuard.Tests.exe'
$report = Join-Path $project ('.artifacts/performance/' + [Guid]::NewGuid().ToString('N'))
$targets = @()
if ($Baseline) { $targets += [PSCustomObject]@{ Name = 'baseline'; Exe = [IO.Path]::GetFullPath($Baseline) } }
$targets += [PSCustomObject]@{ Name = 'current'; Exe = (Join-Path $project 'dist/黑屏守护.exe') }
foreach ($target in $targets) {
    $directory = Join-Path $report $target.Name
    $process = Start-Process -FilePath $helper -ArgumentList @('--measure', ('"' + $target.Exe + '"'), ('"' + $directory + '"')) -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(35000)) { throw '资源测量超时。' }
        if ($process.ExitCode -ne 0) {
            Get-Content -LiteralPath (Join-Path $directory 'error.txt') -ErrorAction SilentlyContinue
            throw "资源测量失败：$($target.Name)"
        }
        Get-Content -LiteralPath (Join-Path $directory 'performance.txt')
    } finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
}
Write-Output "测量结果：$report"
