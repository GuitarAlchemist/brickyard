# dev-deploy.ps1 must not move the live window by accident: on made-up MCP configs in a temporary folder, it stops
# before building when the window would run from another place than the config's -scene-source, says where both are,
# and leaves the config as it was; it goes on when the place stays the same, or with -MoveWindow. Runs in the Windows
# PowerShell 5.1 users deploy from, and builds nothing (-GuardOnly).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$deploy = Join-Path $root 'dev-deploy.ps1'
$tmp = Join-Path ([IO.Path]::GetTempPath()) "deploy-guard-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory $tmp | Out-Null
$failed = 0
function Config([string[]]$sceneArgs) {
    $path = Join-Path $tmp 'mcp.json'
    $list = @('-mcp', '-home', 'C:\made-up\plugindata') + $sceneArgs
    $json = @{ mcpServers = @{ delta = @{ command = 'C:\made-up\brain.exe'; args = $list } } } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($path, $json)
    $path
}
function Expect([string]$what, [string[]]$sceneArgs, [string[]]$flags, [string]$want) {
    $path = Config $sceneArgs
    $before = [IO.File]::ReadAllText($path)
    $switches = @{ GuardOnly = $true }
    foreach ($f in $flags) { $switches[$f.TrimStart('-')] = $true }
    $out = try { & $deploy -PluginData $tmp -McpJson $path @switches | Out-String } catch { $_.Exception.Message }
    $kept = [IO.File]::ReadAllText($path) -eq $before
    $built = @(Get-ChildItem $tmp -Directory -Filter 'delta-*').Count -gt 0
    $ok = $out -match [regex]::Escape($want) -and $kept -and -not $built
    if (-not $ok) { $script:failed++ }
    '{0} {1}' -f $(if ($ok) { 'ok  ' } else { "FAIL ($($out.Trim()))" }), $what
}
try {
    $here = Join-Path $root 'scene'
    Expect 'a deploy from another checkout does not move the window there' @('-scene-source', 'C:\elsewhere\scene') @('-HotReload') 'the live window runs from C:\elsewhere\scene; this deploy would move it to'
    Expect 'a deploy without -HotReload does not swap the window for the published scene' @('-scene-source', $here) @() 'would move it to the published scene, without modules'
    Expect 'a deploy with -HotReload does not take a published window to a checkout' @() @('-HotReload') 'the live window runs from the published scene'
    Expect 'the same checkout, spelt with another case and a slash, keeps the window' @('-scene-source', ($here.ToUpperInvariant() + '\')) @('-HotReload') 'DEPLOY GUARD ok'
    Expect 'a published window stays published' @() @() 'DEPLOY GUARD ok'
    Expect '-MoveWindow moves it on purpose' @('-scene-source', 'C:\elsewhere\scene') @('-HotReload', '-MoveWindow') 'DEPLOY GUARD ok'
} finally {
    Remove-Item -Recurse -Force $tmp
}
if ($failed) { "DEPLOY GUARD CHECK failed: $failed"; exit 1 }
'DEPLOY GUARD CHECK ok'
