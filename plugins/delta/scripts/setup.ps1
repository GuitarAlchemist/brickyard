# Sets Delta up as Claude Code's voice, in the plugin's data folder (-Data, which the delta:setup skill passes):
#
#   delta-<version>\  Delta's window and brain: this repository's release for the plugin's version
#   piper\            Piper, the voice engine (rhasspy/piper 2023.11.14-2, MIT; its Windows build includes
#                     espeak-ng, GPL-3.0)
#   voices\           fr_FR-siwis-medium (rhasspy/piper-voices; dataset CC BY 4.0, fine-tuned from the lessac voice)
#   whisper\          whisper.cpp's server (b5454, MIT) and the ggml-large-v3-turbo-q5_0 model (MIT)
#
# Each download is checked against its SHA-256: pinned below for the third-party files, read from GitHub's release
# metadata for Delta's own zip. -Check reports what is there and what would be downloaded, and changes nothing.
#
# Copies the user already has can be named instead (-Piper, -Voice, -Whisper, -WhisperModel): they are recorded in
# delta.json and not downloaded. -Zip installs a local release zip, to test a build.
param(
    [string]$Data = $env:CLAUDE_PLUGIN_DATA,
    [switch]$Check,
    [string]$Piper = '',
    [string]$Voice = '',
    [string]$Whisper = '',
    [string]$WhisperModel = '',
    [string]$Zip = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar slows Invoke-WebRequest down tenfold
if (-not $Data) { throw 'Name the plugin data folder with -Data.' }
$Data = [IO.Path]::GetFullPath($Data)
$version = (Get-Content (Join-Path $PSScriptRoot '..\.claude-plugin\plugin.json') -Raw | ConvertFrom-Json).version
$deltaDir = Join-Path $Data "delta-$version"

# The user's own copies, recorded by an earlier setup, count as named again while they exist. A recorded copy that is
# gone is forgotten and downloaded.
$saved = @{}
$config = Join-Path $Data 'delta.json'
if (Test-Path $config) {
    (Get-Content $config -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $saved[$_.Name] = $_.Value }
}
foreach ($key in 'piper', 'voice', 'whisper', 'whisper_model') {
    if ($saved.ContainsKey($key) -and -not ($saved[$key] -and (Test-Path ([string]$saved[$key])))) { $saved.Remove($key) }
}
if (-not $Piper) { $Piper = [string]$saved['piper'] }
if (-not $Voice) { $Voice = [string]$saved['voice'] }
if (-not $Whisper) { $Whisper = [string]$saved['whisper'] }
if (-not $WhisperModel) { $WhisperModel = [string]$saved['whisper_model'] }
$voiceConfig = ''
if ($Voice) { $voiceConfig = "$Voice.json" }

$parts = @(
    @{ Key = 'piper'; What = 'Piper, the voice engine'; Own = $Piper; Path = 'piper\piper.exe'; Size = 22477236
        Url = 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip'
        Sha256 = 'f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea'; Folder = 'piper' },
    @{ Key = 'voice'; What = 'the French voice fr_FR-siwis-medium'; Own = $Voice; Path = 'voices\fr_FR-siwis-medium.onnx'; Size = 63201294
        Url = 'https://huggingface.co/rhasspy/piper-voices/resolve/5041a7a49ae5b3a0f30e6837769eb03fdbb231bd/fr/fr_FR/siwis/medium/fr_FR-siwis-medium.onnx'
        Sha256 = '641d1ab097da2b81128c076810edb052b385decc8be3381814802a64a73baf99'; Folder = '' },
    @{ Key = ''; What = "the voice's settings"; Own = $voiceConfig; Path = 'voices\fr_FR-siwis-medium.onnx.json'; Size = 4875
        Url = 'https://huggingface.co/rhasspy/piper-voices/resolve/5041a7a49ae5b3a0f30e6837769eb03fdbb231bd/fr/fr_FR/siwis/medium/fr_FR-siwis-medium.onnx.json'
        Sha256 = '39479916c2db192b5ac9764daddd0c744d83e023ad890c6976c0633ae4df8959'; Folder = '' },
    @{ Key = 'whisper'; What = "whisper.cpp's server"; Own = $Whisper; Path = 'whisper\whisper-server.exe'; Size = 21715607
        Url = 'https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-blas-bin-x64.zip'
        Sha256 = 'b1158bd10edf92c9799149a6fc5d3e58da3f495241d6fb3423088130e88ffbf3'; Folder = 'Release' },
    @{ Key = 'whisper_model'; What = 'the Whisper model large-v3-turbo q5_0'; Own = $WhisperModel; Path = 'whisper\ggml-large-v3-turbo-q5_0.bin'; Size = 574041195
        Url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo-q5_0.bin'
        Sha256 = '394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2'; Folder = '' }
)

function Get-Verified([string]$url, [string]$sha256, [string]$to) {
    $part = "$to.part"
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe -L --fail --silent --show-error -o $part $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
    } else {
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $part
    }
    $got = (Get-FileHash $part -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -ne $sha256) {
        Remove-Item $part
        throw "$url has SHA-256 $got, not $sha256 as expected: nothing was installed from it."
    }
    Move-Item -Force $part $to
}

function Test-Delta {
    $brain = Join-Path $deltaDir 'brain.exe'
    if (-not (Test-Path $brain)) { return $false }
    return ((& $brain -version) -eq $version)
}

# What is there, and what is missing.
$missing = @()
$bytes = 0
$deltaAsset = $null
if (Test-Delta) {
    "Delta $version`: installed in $deltaDir"
} elseif ($Zip) {
    "Delta $version`: to install from $Zip"
} else {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/GuitarAlchemist/brickyard/releases/tags/v$version" -Headers @{ 'User-Agent' = 'delta-setup' }
    $deltaAsset = $release.assets | Where-Object { $_.name -eq "delta-$version-win-x64.zip" }
    if (-not $deltaAsset -or -not $deltaAsset.digest) { throw "The v$version release has no delta-$version-win-x64.zip with a SHA-256." }
    $bytes += $deltaAsset.size
    'Delta {0}: to download, {1:N0} MB' -f $version, ($deltaAsset.size / 1MB)
}
foreach ($part in $parts) {
    $at = Join-Path $Data $part.Path
    if ($part.Own) {
        if (-not (Test-Path $part.Own)) { throw "$($part.What): $($part.Own) does not exist." }
        "$($part.What): yours, $($part.Own)"
    } elseif (Test-Path $at) {
        "$($part.What): installed"
    } else {
        $missing += $part
        $bytes += $part.Size
        '{0}: to download, {1:N0} MB' -f $part.What, ($part.Size / 1MB)
    }
}
'Total to download: {0:N0} MB. Ollama is not needed: Claude is Delta''s brain.' -f ($bytes / 1MB)
if ($Check) { exit 0 }

New-Item -ItemType Directory -Force $Data | Out-Null

# The user's own copies, recorded where the brain looks for them.
foreach ($part in $parts) {
    if ($part.Own -and $part.Key) { $saved[$part.Key] = [IO.Path]::GetFullPath($part.Own) }
}
[IO.File]::WriteAllText($config, ($saved | ConvertTo-Json)) # UTF-8 without the BOM that Go's JSON parser refuses

# The third-party files, each verified before it is unpacked or moved into place.
foreach ($part in $missing) {
    $at = Join-Path $Data $part.Path
    New-Item -ItemType Directory -Force (Split-Path $at) | Out-Null
    "Downloading $($part.What)..."
    if (-not $part.Folder) {
        Get-Verified $part.Url $part.Sha256 $at
        continue
    }
    $archive = Join-Path $Data ('download-{0}.zip' -f $part.Folder)
    $unpack = Join-Path $Data ('unpack-{0}' -f $part.Folder)
    Get-Verified $part.Url $part.Sha256 $archive
    if (Test-Path $unpack) { Remove-Item -Recurse -Force $unpack }
    Expand-Archive -Path $archive -DestinationPath $unpack
    Get-ChildItem (Join-Path $unpack $part.Folder) | Move-Item -Destination (Split-Path $at) -Force
    Remove-Item -Recurse -Force $unpack, $archive
}

# Delta itself, in a folder of its version: a brain still running from another version is left alone.
if (-not (Test-Delta)) {
    $archive = $Zip
    if (-not $Zip) {
        "Downloading Delta $version..."
        $archive = Join-Path $Data "delta-$version-win-x64.zip"
        Get-Verified $deltaAsset.browser_download_url ($deltaAsset.digest -replace '^sha256:', '') $archive
    }
    $unpack = Join-Path $Data "unpack-delta-$version"
    if (Test-Path $unpack) { Remove-Item -Recurse -Force $unpack }
    Expand-Archive -Path $archive -DestinationPath $unpack
    if (Test-Path $deltaDir) { Remove-Item -Recurse -Force $deltaDir }
    Move-Item (Join-Path $unpack "delta-$version-win-x64") $deltaDir
    Remove-Item -Recurse -Force $unpack
    if (-not $Zip) { Remove-Item $archive }
    if (-not (Test-Delta)) { throw "Delta $version did not install in $deltaDir." }
}
foreach ($old in Get-ChildItem $Data -Directory -Filter 'delta-*' | Where-Object { $_.Name -ne "delta-$version" }) {
    try { Remove-Item -Recurse -Force $old.FullName } catch { "Left $($old.FullName) in place: it is in use." }
}

"Delta $version is set up in $Data."
