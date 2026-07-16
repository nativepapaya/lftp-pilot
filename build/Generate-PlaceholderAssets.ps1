[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$MasterIcon
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\LFTPPilot.App\Assets' }
if (-not $MasterIcon) { $MasterIcon = Join-Path $OutputDirectory 'LFTPPilot.Master.png' }

if (-not (Test-Path -LiteralPath $MasterIcon -PathType Leaf)) {
    throw "The reviewed master icon is missing: $MasterIcon"
}

Add-Type -AssemblyName System.Drawing
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$assets = [ordered]@{
    'StoreLogo.png' = @(50, 50)
    'Square44x44Logo.png' = @(44, 44)
    'Square150x150Logo.png' = @(150, 150)
    'Wide310x150Logo.png' = @(310, 150)
    'SplashScreen.png' = @(620, 300)
}

$source = [Drawing.Image]::FromFile((Resolve-Path -LiteralPath $MasterIcon).Path)
try {
    foreach ($entry in $assets.GetEnumerator()) {
        $width = [int]$entry.Value[0]
        $height = [int]$entry.Value[1]
        $bitmap = [Drawing.Bitmap]::new($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality

            if ($width -eq $height) {
                $graphics.Clear([Drawing.Color]::Transparent)
                $destination = [Drawing.Rectangle]::new(0, 0, $width, $height)
            }
            else {
                $graphics.Clear([Drawing.Color]::FromArgb(255, 9, 20, 38))
                $iconSize = [int][Math]::Round([Math]::Min($height * 0.86, $width * 0.42))
                $destination = [Drawing.Rectangle]::new(
                    [int](($width - $iconSize) / 2),
                    [int](($height - $iconSize) / 2),
                    $iconSize,
                    $iconSize)
            }

            $graphics.DrawImage($source, $destination)
            $bitmap.Save((Join-Path $OutputDirectory $entry.Key), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally { $source.Dispose() }

Get-ChildItem -LiteralPath $OutputDirectory -File -Filter '*.png'
