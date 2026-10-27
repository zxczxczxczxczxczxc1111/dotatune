if ($PSVersionTable.PSVersion.Major -lt 6) { throw 'Run with pwsh 7' }
# Builds src\DotaTune\Assets\dotatune.ico from the spider drawings in build\icon: 16 and 32
# have simplified drawings of their own, the larger sizes are scaled down from the 256 master.
# Chrome renders the SVGs. The .ico holds PNG images, which Windows reads from Vista on.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'build\icon'
$target = Join-Path $root 'src\DotaTune\Assets\dotatune.ico'
$chrome = @(
    (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
    (Join-Path $env:LOCALAPPDATA 'Google\Chrome\Application\chrome.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $chrome) { throw 'Chrome not found, it renders the SVG drawings' }

# Short folder: Chrome and GDI+ both fail on paths past 260 characters.
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('dti' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

function Render([int] $side) {
    $png = Join-Path $work "pauk-$side.png"
    $svg = (Join-Path $source "pauk-$side.svg") -replace '\\', '/'
    # chrome.exe is a window program, so the shell does not wait for it on its own.
    $p = Start-Process -FilePath $chrome -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
        '--headless', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000', '--force-device-scale-factor=1',
        "--user-data-dir=$work\profile", "--window-size=$side,$side", "--screenshot=$png", "file:///$svg")
    if ($p.ExitCode -ne 0) { throw "Chrome exited with code $($p.ExitCode) on pauk-$side.svg" }
    if (-not (Test-Path -LiteralPath $png)) { throw "Chrome did not render pauk-$side.svg" }
    return [System.Drawing.Bitmap]::FromFile($png)
}

function Scale([System.Drawing.Bitmap] $from, [int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($from, 0, 0, $size, $size)
    $g.Dispose()
    return $bitmap
}

function PngBytes([System.Drawing.Bitmap] $bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $stream.ToArray()
}

$drawn = @{}
try {
    foreach ($side in 16, 32, 256) { $drawn[$side] = Render $side }
    # Each size comes from the smallest drawing that is not smaller than it.
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = foreach ($size in $sizes) {
        $nearest = @(16, 32, 256) | Where-Object { $_ -ge $size } | Select-Object -First 1
        $from = $drawn[$nearest]
        if ($from.Width -eq $size) { , (PngBytes $from) }
        else { $scaled = Scale $from $size; , (PngBytes $scaled); $scaled.Dispose() }
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$side); $w.Write([byte]$side); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $w.Write($image) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($target, $out.ToArray())
    $w.Dispose()
}
finally {
    # The bitmaps hold their PNG files open, the folder goes only after them.
    foreach ($bitmap in $drawn.Values) { $bitmap.Dispose() }
    try { Remove-Item -LiteralPath $work -Recurse -Force }
    catch { Write-Warning "Temporary folder $work stays: $($_.Exception.Message)" }
}
Get-Item -LiteralPath $target | Select-Object FullName, Length
