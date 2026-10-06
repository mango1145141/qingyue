Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$iconDirectory = Join-Path $PSScriptRoot 'EpubKindleFix-project\Assets'
if (-not (Test-Path -LiteralPath $iconDirectory)) { $iconDirectory = Join-Path $PSScriptRoot 'Assets' }
$iconFrames = New-Object 'System.Collections.Generic.List[byte[]]'
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
function Render-QingyueIcon([int]$size) {
    # Reuse the actual WPF header logo geometry, colours, and proportions.
    $markup = '<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="43" Height="43" CornerRadius="13" Background="#57799D"><Viewbox Width="26" Height="24"><Path Data="M 20,6 C 12,1 5,2 0,4 L 0,30 C 7,28 13,29 20,33 C 27,29 33,28 40,30 L 40,4 C 35,2 28,1 20,6 M 20,6 L 20,33" Stroke="#F7F5ED" StrokeThickness="2.4" StrokeLineJoin="Round" /></Viewbox></Border>'
    $logo = [System.Windows.Markup.XamlReader]::Parse($markup)
    $frame = New-Object System.Windows.Controls.Viewbox
    $frame.Width = $size
    $frame.Height = $size
    $frame.Child = $logo
    $frame.Measure((New-Object System.Windows.Size($size, $size)))
    $frame.Arrange((New-Object System.Windows.Rect(0, 0, $size, $size)))
    $frame.UpdateLayout()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($frame)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}
[IO.File]::WriteAllBytes((Join-Path $iconDirectory 'KindleIcon.png'), (Render-QingyueIcon 512))
foreach ($size in $iconSizes) { $iconFrames.Add((Render-QingyueIcon $size)) }
$iconStream = [IO.File]::Create((Join-Path $iconDirectory 'KindleIcon.ico'))
$iconWriter = New-Object IO.BinaryWriter($iconStream)
try {
    $iconWriter.Write([uint16]0)
    $iconWriter.Write([uint16]1)
    $iconWriter.Write([uint16]$iconSizes.Count)
    $offset = 6 + 16 * $iconSizes.Count
    for ($i = 0; $i -lt $iconSizes.Count; $i++) {
        $dimension = if ($iconSizes[$i] -eq 256) { 0 } else { $iconSizes[$i] }
        $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1)
        $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$iconFrames[$i].Length)
        $iconWriter.Write([uint32]$offset)
        $offset += $iconFrames[$i].Length
    }
    foreach ($bytes in $iconFrames) { $iconWriter.Write($bytes) }
} finally { $iconWriter.Dispose(); $iconStream.Dispose() }
Write-Output 'Saved the existing blue book logo as PNG and multi-size ICO.'
