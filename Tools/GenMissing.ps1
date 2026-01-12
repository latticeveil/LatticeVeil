Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(16, 16)
for ($y=0; $y -lt 16; $y++) {
    for ($x=0; $x -lt 16; $x++) {
        $isPurple = ([Math]::Floor($x / 4) + [Math]::Floor($y / 4)) % 2 -eq 0
        $color = if ($isPurple) { [System.Drawing.Color]::FromArgb(255, 0, 255) } else { [System.Drawing.Color]::FromArgb(20, 20, 20) }
        $bmp.SetPixel($x, $y, $color)
    }
}
$bmp.Save("C:\Users\Redacted\Documents\redactedcraft.github.io\assets\img\missing.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "Generated purple/black missing.png"
