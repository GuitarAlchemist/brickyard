# The gallery's videos (scene/GalleryVideo.cs): for each demo of gallery.json whose page shows a video after its #anchor,
# or anywhere on a page without one (<video><source src="...mp4">), that video fetched over HTTPS and cut by FFmpeg as its picture is
# (pictures.ps1: 4:3 from the middle, stretched to 512 x 512), at 24 images a second without sound, saved in
# %LOCALAPPDATA%\delta\gallery\videos under the same key as its picture (the first 16 hex digits of the SHA-1 of its
# URL's [Uri]::AbsoluteUri). They stay on this machine. A video already there is kept; -Again fetches it anew.
#
#   & tools/gallery-pictures/videos.ps1
param(
    [string]$Gallery = (Join-Path $PSScriptRoot '..\..\gallery\gallery.json'),
    [string]$Out = (Join-Path $env:LOCALAPPDATA 'delta\gallery\videos'),
    [switch]$Again)
$ErrorActionPreference = 'Stop'
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw 'ffmpeg is not on PATH (winget install Gyan.FFmpeg)' }
New-Item -ItemType Directory -Force $Out | Out-Null

function Get-Key([string]$url) {
    $abs = ([Uri]$url).AbsoluteUri
    (([Security.Cryptography.SHA1]::HashData([Text.Encoding]::UTF8.GetBytes($abs)) | Select-Object -First 8 | ForEach-Object { $_.ToString('x2') }) -join '')
}

# The first video source after the anchor's id on the page and before the next id'd heading, or the page's first
# without an anchor, as an absolute URL.
function Find-Video([string]$html, [Uri]$page, [string]$anchor) {
    if ($anchor) {
        $at = $html.IndexOf("id=`"$anchor`"", [StringComparison]::Ordinal)
        if ($at -lt 0) { return $null }
        $next = [regex]::Match($html.Substring($at + 4), '<h[1-6][^>]* id="')
        $part = if ($next.Success) { $html.Substring($at, $next.Index + 4) } else { $html.Substring($at) }
    } else { $part = $html }
    $m = [regex]::Match($part, '<video[^>]*>[\s\S]*?<source[^>]*\ssrc="([^"]+\.mp4)"')
    if (-not $m.Success) { return $null }
    [Uri]::new($page, [Net.WebUtility]::HtmlDecode($m.Groups[1].Value))
}

$pages = @{}
foreach ($demo in (Get-Content $Gallery -Raw -Encoding utf8 | ConvertFrom-Json).demos) {
    $uri = [Uri]$demo.url
    if ($uri.Scheme -ne 'https') { continue }
    $base = $uri.GetLeftPart([UriPartial]::Query)
    if (-not $pages.ContainsKey($base)) {
        try { $pages[$base] = (Invoke-WebRequest -UseBasicParsing $base).Content }
        catch { "skipped $($demo.title): $($_.Exception.Message)"; $pages[$base] = ''; continue }
    }
    $anchor = ''
    if ($uri.Fragment) { $anchor = [Uri]::UnescapeDataString($uri.Fragment.Substring(1)) } # no ?: : check.ps1 parses as Windows PowerShell
    $video = Find-Video $pages[$base] ([Uri]$base) $anchor
    if (-not $video) { continue }
    if ($video.Scheme -ne 'https') { "skipped $($demo.title): $video is not HTTPS"; continue }
    $key = Get-Key $demo.url
    $file = Join-Path $Out "$key.mp4"
    if ((Test-Path $file) -and -not $Again) { "kept $($demo.title) as $key"; continue }
    $raw = Join-Path ([IO.Path]::GetTempPath()) ("gallery-video-" + [guid]::NewGuid().ToString('n') + '.mp4')
    try {
        Invoke-WebRequest -UseBasicParsing $video -OutFile $raw
        $cut = Join-Path $Out "$key.part.mp4"
        & $ffmpeg -hide_banner -loglevel error -y -i $raw -an -vf 'crop=min(iw\,ih*4/3):min(ih\,iw*3/4),scale=512:512:flags=bicubic,fps=24' `
            -c:v libx264 -preset slow -crf 23 -pix_fmt yuv420p -movflags +faststart $cut
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $video" }
        Move-Item -Force $cut $file
        "saved $($demo.title) as $key ($([math]::Round((Get-Item $file).Length / 1MB, 1)) MB)"
    } finally {
        if (Test-Path $raw) { Remove-Item $raw }
    }
}
