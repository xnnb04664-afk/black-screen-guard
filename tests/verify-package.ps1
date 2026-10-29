$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$project = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $project 'dist'
$executable = Join-Path $dist '黑屏守护.exe'
$version = [Reflection.AssemblyName]::GetAssemblyName($executable).Version.ToString(3)
$archive = Join-Path $dist "BlackScreenGuard-v$version-win.zip"
$package = [IO.Compression.ZipFile]::OpenRead($archive)
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $expected = @('黑屏守护.exe', '黑屏守护.exe.config', '使用说明.md')
    if ($package.Entries.Count -ne $expected.Count) { throw '便携包内容不符合预期。' }
    foreach ($name in $expected) {
        $entry = $package.GetEntry($name)
        if ($null -eq $entry) { throw "便携包缺少 $name" }
        $stream = $entry.Open()
        try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose() }
        $localHash = (Get-FileHash -LiteralPath (Join-Path $dist $name) -Algorithm SHA256).Hash
        if ($actual -ne $localHash) { throw "便携包中的 $name 与已验证文件不同。" }
        Write-Output "PASS: packaged $name matches verified local file"
    }
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums = Get-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt')
    if ($checksums -notcontains ($archiveHash + '  ' + [IO.Path]::GetFileName($archive))) { throw 'ZIP 校验和不匹配。' }
    Write-Output 'PASS: archive SHA-256 matches SHA256SUMS.txt'
} finally { $sha.Dispose(); $package.Dispose() }
