# Renders the app icon (teal rounded tile with "IO", as in the sidebar logo) into src\IoRecorder.App\app.ico.
# Run with Windows PowerShell; it uses WPF to draw each size as a PNG frame of a multi-size .ico.
param([string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\IoRecorder.App\app.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
function Render([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $radius = [Math]::Max(2, $size * 0.22)
    $tile = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x65, 0xDD, 0xC8))
    $dc.DrawRoundedRectangle($tile, $null, (New-Object System.Windows.Rect 0, 0, $size, $size), $radius, $radius)
    $face = New-Object System.Windows.Media.Typeface (New-Object System.Windows.Media.FontFamily 'Segoe UI'), ([System.Windows.FontStyles]::Normal), ([System.Windows.FontWeights]::Bold), ([System.Windows.FontStretches]::Normal)
    $ink = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x0A, 0x29, 0x23))
    $text = New-Object System.Windows.Media.FormattedText 'IO', ([Globalization.CultureInfo]::InvariantCulture), ([System.Windows.FlowDirection]::LeftToRight), $face, ($size * 0.52), $ink
    $dc.DrawText($text, (New-Object System.Windows.Point (($size - $text.Width) / 2), (($size - $text.Height) / 2)))
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream; $encoder.Save($stream)
    return , $stream.ToArray()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = @($sizes | ForEach-Object { , (Render $_) })
$out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $edge = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$edge); $w.Write([byte]$edge); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Flush(); [IO.File]::WriteAllBytes($Output, $out.ToArray())
Write-Host "Icon: $Output ($($sizes -join ', ') px)"
