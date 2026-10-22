# Builds this checkout into a plugin-data folder of its own and points a Claude Code MCP config at it, to try a change
# on the live Delta. The folder and the version carry git describe, with -dirty and the time for uncommitted work, so
# that the journal's start event says exactly what runs.
#
#   .\dev-deploy.ps1 -PluginData <the folder holding delta.json> [-McpJson <.mcp.json>] [-Triage qwen3:8b]
#
# -Triage replaces the config's -triage model; without it the config keeps its own. Then reconnect delta in Claude
# Code's /mcp. The new brain records itself in %LOCALAPPDATA%\delta\brain.pid and the brain it replaces stops by
# itself; a brain built before brain.pid existed does not, and must be stopped by hand.
param(
    [Parameter(Mandatory = $true)][string]$PluginData,
    [string]$McpJson,
    [string]$Triage
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$PluginData = (Resolve-Path $PluginData).Path

$describe = (& git -C $root describe --tags --always --dirty).Trim()
if ($describe -like '*-dirty') { $describe += '-' + (Get-Date -Format 'HHmmss') }
$version = "dev-$describe"
$out = Join-Path $PluginData "delta-$version"
New-Item -ItemType Directory -Force $out | Out-Null

Push-Location (Join-Path $root 'brain')
try {
    & go build -trimpath -ldflags "-X main.version=$version" -o (Join-Path $out 'brain.exe') .
    if ($LASTEXITCODE -ne 0) { throw 'go build failed' }
} finally { Pop-Location }

Push-Location (Join-Path $root 'scene')
try {
    & dotnet publish -c Release -r win-x64 --self-contained -p:DisableTransitiveFrameworkReferenceDownloads=true -p:DebugType=none -o (Join-Path $out 'scene') -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
} finally { Pop-Location }

$brain = Join-Path $out 'brain.exe'
$built = (& $brain -version | Out-String).Trim()
if ($built -ne $version) { throw "brain.exe says $built, not $version" }
Write-Host "Built $version in $out"

if (-not $McpJson) {
    Write-Host "Next: run $brain -mcp -home $PluginData from your MCP config, then reconnect delta in /mcp."
    return
}
$path = (Resolve-Path $McpJson).Path
$config = Get-Content $path -Raw | ConvertFrom-Json
$server = $config.mcpServers.delta
if (-not $server) { throw "$McpJson has no mcpServers.delta" }
$server.command = $brain
if ($Triage) {
    $kept = New-Object System.Collections.Generic.List[string]
    $old = @($server.args)
    for ($i = 0; $i -lt $old.Count; $i++) {
        if ($old[$i] -eq '-triage') { $i++; continue } # its value follows it
        $kept.Add($old[$i])
    }
    $kept.Add('-triage')
    $kept.Add($Triage)
    $server.args = $kept.ToArray()
}
$json = $config | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($path, $json + "`n", (New-Object Text.UTF8Encoding $false))
Write-Host "Pointed $McpJson at it. Next: reconnect delta in Claude Code's /mcp."
