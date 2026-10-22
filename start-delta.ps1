# Starts Delta: the robot's scene, then its brain, listening on the default microphone. Nothing is downloaded: each
# program Delta needs is looked for where README.md says to put it, or given as a parameter.
#
#   .\start-delta.ps1 [-Type] [-Voice <voice.onnx>] [-Model qwen3:8b] [-Piper <piper.exe>]
#                     [-Whisper <whisper-server.exe>] [-WhisperModel <ggml.bin>] [-Ollama http://127.0.0.1:11434]
#
# -Type reads typed sentences instead of listening. Ctrl+C stops Delta.
param(
    [switch]$Type,
    [string]$Voice = (Join-Path $PSScriptRoot 'voices\fr_FR-siwis-medium.onnx'),
    [string]$Model = 'qwen3:8b',
    [string]$Piper = (Join-Path $PSScriptRoot 'piper\piper.exe'),
    [string]$Whisper = (Join-Path $PSScriptRoot 'whisper\whisper-server.exe'),
    [string]$WhisperModel = '',
    [string]$Ollama = 'http://127.0.0.1:11434'
)
$ErrorActionPreference = 'Stop'
$scenePort = 8805

function Test-Port([int]$port) {
    $client = New-Object Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $port); return $true } catch { return $false } finally { $client.Close() }
}

$missing = @()
if (-not (Test-Path $Piper)) { $missing += "Piper: $Piper" }
if (-not (Test-Path $Voice) -or -not (Test-Path "$Voice.json")) { $missing += "a Piper voice and its .onnx.json: $Voice" }
if (-not $Type) {
    if ($WhisperModel -eq '') { $WhisperModel = Join-Path (Split-Path $Whisper) 'ggml-large-v3-turbo-q5_0.bin' }
    if (-not (Test-Path $Whisper)) { $missing += "whisper.cpp's server: $Whisper" }
    if (-not (Test-Path $WhisperModel)) { $missing += "a Whisper model: $WhisperModel" }
}
try {
    $tags = Invoke-RestMethod -Uri "$Ollama/api/tags" -TimeoutSec 5
    if (-not ($tags.models | Where-Object { $_.name -eq $Model })) { $missing += "the model ${Model}: run  ollama pull $Model" }
} catch {
    $missing += "Ollama, at ${Ollama}: install it from https://ollama.com and start it"
}
if ($missing.Count -gt 0) {
    Write-Host 'Delta cannot start yet; README.md, "What you need", says where to get each of these:'
    $missing | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
if (Test-Port $scenePort) {
    Write-Host "Port $scenePort is already taken: is Delta already running?"
    exit 1
}

# The scene shares this console, its output going to a log, so that Ctrl+C stops it with the brain.
$log = Join-Path $env:TEMP 'delta-scene.log'
$scene = Start-Process -FilePath (Join-Path $PSScriptRoot 'scene\scene.exe') -ArgumentList '--listen', $scenePort `
    -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
$deadline = (Get-Date).AddSeconds(20)
while (-not (Test-Port $scenePort)) {
    if ($scene.HasExited -or (Get-Date) -gt $deadline) {
        Write-Host "The scene did not start (it needs a GPU with Vulkan 1.3); see $log and $log.err"
        exit 1
    }
    Start-Sleep -Milliseconds 200
}

$brainArgs = @('-piper', $Piper, '-voice', $Voice, '-model', $Model, '-ollama', $Ollama, '-scene', "127.0.0.1:$scenePort")
if ($Type) {
    Write-Host 'Type a sentence and press Enter; Ctrl+C stops Delta.'
} else {
    $brainArgs += @('-whisper', $Whisper, '-whisper-model', $WhisperModel, '-mic')
    Write-Host 'Delta is getting ready (the first time, the model takes a while to load), then listens. Ctrl+C stops it.'
}
try {
    & (Join-Path $PSScriptRoot 'brain.exe') @brainArgs
} finally {
    if (-not $scene.HasExited) { Stop-Process -Id $scene.Id }
}
