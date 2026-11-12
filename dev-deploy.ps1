# Builds this checkout into a plugin-data folder of its own and points a Claude Code MCP config at it, to try a change
# on the live Delta. The folder and the version carry git describe, with -dirty and the time for uncommitted work, so
# that the journal's start event says exactly what runs.
#
#   .\dev-deploy.ps1 -PluginData <the folder holding delta.json> [-McpJson <.mcp.json>] [-Triage qwen3:8b] [-HotReload]
#
# -Triage replaces the config's -triage model; without it the config keeps its own. -HotReload runs Delta's window from
# this checkout's scene\ under dotnet watch, so that an edit there changes the open window; without it the window is
# the one built here. Then reconnect delta in Claude
# Code's /mcp. The new brain records itself in %LOCALAPPDATA%\delta\brain.pid and the brain it replaces stops by
# itself; a brain built before brain.pid existed does not, and must be stopped by hand.
#
# When the config already runs the window from a -scene-source other than the one this deploy would give it (this
# checkout's scene\ with -HotReload, none without), the deploy stops before building: deploying a brain from another
# checkout would otherwise move the live window there too, or to the published scene, which has no modules. Deploy from
# the checkout the window runs from, or pass -MoveWindow to move it.
param(
    [Parameter(Mandatory = $true)][string]$PluginData,
    [string]$McpJson,
    [string]$Triage,
    [switch]$HotReload,
    [switch]$MoveWindow,
    [switch]$GuardOnly # stops after that check, for check-deploy-guard.ps1
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$PluginData = (Resolve-Path $PluginData).Path

if ($McpJson -and -not $MoveWindow) {
    $before = @((Get-Content (Resolve-Path $McpJson).Path -Raw | ConvertFrom-Json).mcpServers.delta.args)
    $i = [Array]::IndexOf($before, '-scene-source')
    $runs = if ($i -ge 0 -and $i + 1 -lt $before.Count) { $before[$i + 1] } else { '' }
    $would = if ($HotReload) { Join-Path $root 'scene' } else { '' }
    $same = ($runs -eq '' -and $would -eq '') -or ($runs -ne '' -and $would -ne '' -and
        [IO.Path]::GetFullPath($runs).TrimEnd('\') -ieq [IO.Path]::GetFullPath($would).TrimEnd('\'))
    if (-not $same) {
        $from = if ($runs) { $runs } else { 'the published scene' }
        $to = if ($would) { $would } else { 'the published scene, without modules' }
        throw "the live window runs from $from; this deploy would move it to $to. Deploy from that checkout, or pass -MoveWindow"
    }
}
if ($GuardOnly) {
    'DEPLOY GUARD ok: the window stays where it runs'
    return
}

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
$kept = New-Object System.Collections.Generic.List[string]
$old = @($server.args)
for ($i = 0; $i -lt $old.Count; $i++) {
    if ($old[$i] -eq '-scene-source') { $i++; continue }
    $kept.Add($old[$i])
}
if ($HotReload) {
    $kept.Add('-scene-source')
    $kept.Add((Join-Path $root 'scene'))
}
$server.args = $kept.ToArray()
$json = $config | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($path, $json + "`n", (New-Object Text.UTF8Encoding $false))
Write-Host "Pointed $McpJson at it. Next: reconnect delta in Claude Code's /mcp."
