$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot 'assets'
$source = [Drawing.Image]::FromFile((Join-Path $assets 'app-icon.png'))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($source, [Drawing.Rectangle]::new(0, 0, $size, $size))
            } finally { $graphics.Dispose() }
            $pixels = [byte[]]::new($size * $size * 4)
            $data = $bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $size, $size), [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                for ($row = 0; $row -lt $size; $row++) {
                    [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $row * $data.Stride), $pixels, ($size - 1 - $row) * $size * 4, $size * 4)
                }
            } finally { $bitmap.UnlockBits($data) }
            $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
            $mask = [byte[]]::new($maskStride * $size)
            for ($row = 0; $row -lt $size; $row++) {
                for ($column = 0; $column -lt $size; $column++) {
                    if ($pixels[($row * $size + $column) * 4 + 3] -eq 0) {
                        $index = $row * $maskStride + [int][Math]::Floor($column / 8.0)
                        $mask[$index] = $mask[$index] -bor (1 -shl (7 - ($column % 8)))
                    }
                }
            }
            $frame = [IO.MemoryStream]::new()
            $writer = [IO.BinaryWriter]::new($frame)
            try {
                $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
                $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([int]0)
                $writer.Write([int]($pixels.Length + $mask.Length))
                1..4 | ForEach-Object { $writer.Write([int]0) }
                $writer.Write($pixels); $writer.Write($mask)
                $frames.Add($frame.ToArray())
            } finally { $writer.Dispose(); $frame.Dispose() }
        } finally { $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
$output = [IO.File]::Create((Join-Path $assets 'app.ico'))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([int]$frames[$index].Length); $writer.Write([int]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $output.Dispose() }
Get-Item -LiteralPath (Join-Path $assets 'app.ico') | Select-Object FullName, Length
