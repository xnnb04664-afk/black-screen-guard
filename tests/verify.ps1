$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$report = Join-Path $project ('.artifacts/verification/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
& (Join-Path $project 'build.ps1') -TestBuild
$exe = Join-Path $project '.artifacts/test/BlackScreenGuard.Tests.exe'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class GuardTestRecovery {
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint id);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h, int i, StringBuilder s, uint n, out uint needed);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr OpenDesktop(string n, uint f, bool inherit, uint access);
    [DllImport("user32.dll")] public static extern bool SwitchDesktop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
}
'@
$desktopName = New-Object Text.StringBuilder 256
$needed = [uint32]0
if (-not [GuardTestRecovery]::GetUserObjectInformation([GuardTestRecovery]::GetThreadDesktop([GuardTestRecovery]::GetCurrentThreadId()), 2, $desktopName, 512, [ref]$needed)) { throw '无法保存测试前的桌面。' }
$desktopHandle = [GuardTestRecovery]::OpenDesktop($desktopName.ToString(), 0, $false, 0x01FF)
if ($desktopHandle -eq [IntPtr]::Zero) { throw '无法准备测试恢复。' }
$testProcess = $null
try {
    $testProcess = Start-Process -FilePath $exe -ArgumentList @('--self-test', ('"' + $report + '"')) -PassThru -WindowStyle Hidden
    if (-not $testProcess.WaitForExit(60000)) { throw '测试超时，正在恢复桌面。' }
    Get-Content -LiteralPath (Join-Path $report 'tests.txt')
    if ($testProcess.ExitCode -ne 0) { throw "测试失败：$($testProcess.ExitCode)" }
    Write-Output "验证结果：$report"
} finally {
    [void][GuardTestRecovery]::SwitchDesktop($desktopHandle)
    [void][GuardTestRecovery]::CloseDesktop($desktopHandle)
    if ($null -ne $testProcess -and -not $testProcess.HasExited) { Stop-Process -Id $testProcess.Id }
}
