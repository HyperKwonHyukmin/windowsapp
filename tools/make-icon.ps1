# DeskKeeper 아이콘(.ico) 생성 — PowerShell 7 에서 실행
# 크기별 PNG 를 그려 ICO 컨테이너로 묶는다(Vista 이후 PNG 항목 지원)
param([string]$Out = "$PSScriptRoot\..\src\DeskKeeper.Tray\DeskKeeper.ico")

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 48, 256
$pngs = foreach ($s in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'

    # 둥근 사각형 배경
    $r = [Math]::Max(2, [int]($s * 0.22))
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2; $w = $s - 1
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 15, 118, 110)), $path)

    # 가운데 흰색 굵은 D
    $font = [System.Drawing.Font]::new('Segoe UI', [single]($s * 0.68), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = [System.Drawing.StringFormat]::new()
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $rect = [System.Drawing.RectangleF]::new(0, [single]($s * 0.03), $s, $s)
    $g.DrawString('D', $font, [System.Drawing.Brushes]::White, $rect, $fmt)

    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    , $ms.ToArray()
}

# ICO 헤더 + 디렉터리 + PNG 데이터
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf))
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))   # 256 은 0 으로 기록
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
"생성: $Out"
