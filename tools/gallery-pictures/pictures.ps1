# The gallery's pictures (scene/GalleryPictures.cs): each demo's page screenshotted in a throwaway headless Chromium
# (shoot.ps1: muted, no permission granted, its own profile, stopped after), cropped 4:3 and stretched to 512 x 512,
# saved in %LOCALAPPDATA%\delta\gallery\pictures under the first 16 hex digits of the SHA-1 of its URL as the scene reads
# it ([Uri]::AbsoluteUri). A documentation page is cropped to its first video, image or canvas after its #anchor, or
# its largest one. -Cut name=top,bottom trims fractions first (a HUD, a video's controls); -Top name crops from the top.
# Artifacts are not fetched here: claude.ai refuses headless browsers. Render each one's files locally (the Artifact
# tool's read with path), shoot that with shoot.ps1, then -From <png> -Url <its url> saves it under its key.
#
# From pwsh (-Cut takes a hashtable, which pwsh -File cannot pass):
#   & tools/gallery-pictures/pictures.ps1 -Cut @{ demo00 = '0.12,0'; demo07 = '0,0.14' }   # every demo of gallery.json
#   & tools/gallery-pictures/pictures.ps1 -From art.png -Url https://claude.ai/artifact/...
param(
    [string]$Gallery = (Join-Path $PSScriptRoot '..\..\gallery\gallery.json'),
    [string]$Out = (Join-Path $env:LOCALAPPDATA 'delta\gallery\pictures'),
    [string]$From, [string]$Url, [int]$Wait = 15,
    [hashtable]$Cut = @{}, [string[]]$Top = @())
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

function Key([string]$url) {
    $abs = ([Uri]$url).AbsoluteUri
    (([Security.Cryptography.SHA1]::HashData([Text.Encoding]::UTF8.GetBytes($abs)) | Select-Object -First 8 | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Save([string]$png, [string]$url, [string]$name) {
    $b = [Drawing.Bitmap]::new($png)
    try {
        $c = if ($Cut[$name]) { [double[]]($Cut[$name] -split ',') } else { @(0.0, 0.0) }
        $y0 = [int]($b.Height * $c[0]); $h = [int]($b.Height * (1 - $c[0] - $c[1])); $w = $b.Width; $x0 = 0
        if ($w / $h -gt 4 / 3) { $nw = [int]($h * 4 / 3); $x0 = [int](($w - $nw) / 2); $w = $nw }
        else { $nh = [int]($w * 3 / 4); if ($Top -notcontains $name) { $y0 += [int](($h - $nh) / 2) }; $h = $nh }
        $t = [Drawing.Bitmap]::new(512, 512)
        $g = [Drawing.Graphics]::FromImage($t)
        $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
        $g.DrawImage($b, [Drawing.Rectangle]::new(0, 0, 512, 512), [Drawing.Rectangle]::new($x0, $y0, $w, $h), 'Pixel')
        $key = Key $url
        $t.Save((Join-Path $Out "$key.png"), [Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $t.Dispose()
        "saved $name as $key"
    } finally { $b.Dispose() }
}

if ($From) { Save $From $Url (Split-Path $From -LeafBase); return }

$demos = (Get-Content $Gallery -Raw -Encoding utf8 | ConvertFrom-Json).demos
$pages = @{}
for ($i = 0; $i -lt $demos.Count; $i++) { $pages['demo{0:D2}' -f $i] = $demos[$i].url }
$shots = Join-Path ([IO.Path]::GetTempPath()) ('gallery-shots-' + [guid]::NewGuid().ToString('n'))
& (Join-Path $PSScriptRoot 'shoot.ps1') -Out $shots -Pages $pages -Wait $Wait
foreach ($name in $pages.Keys | Sort-Object) {
    $png = Join-Path $shots "$name.png"
    if (Test-Path $png) { Save $png $pages[$name] $name } else { "no shot of $name" }
}
