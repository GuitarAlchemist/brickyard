# Checks that the marketplace, the delta plugin and the release agree, and that every PowerShell script parses in the
# PowerShell running this one (CI runs it under Windows PowerShell 5.1, which users have). -Version also checks that
# the plugin is at that version: the release workflow passes the tag's.
param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$failures = @()

$marketplace = Get-Content (Join-Path $root '.claude-plugin\marketplace.json') -Raw | ConvertFrom-Json
foreach ($entry in $marketplace.plugins) {
    if (-not (Test-Path (Join-Path $root "$($entry.source)\.claude-plugin\plugin.json"))) {
        $failures += "marketplace.json: $($entry.name)'s source $($entry.source) has no .claude-plugin\plugin.json"
    }
}

$plugin = Get-Content (Join-Path $root 'plugins\delta\.claude-plugin\plugin.json') -Raw | ConvertFrom-Json
$command = $plugin.mcpServers.delta.command
if ($command -ne "`${CLAUDE_PLUGIN_DATA}/delta-$($plugin.version)/brain.exe") {
    $failures += "plugin.json: the delta server runs $command, not the brain setup.ps1 installs for version $($plugin.version)"
}
if (@($plugin.channels | Where-Object { $_.server -eq 'delta' }).Count -ne 1) {
    $failures += 'plugin.json: no channel for the delta server'
}
if ($Version -and $plugin.version -ne $Version) {
    $failures += "plugin.json is at version $($plugin.version), not $Version"
}
# A tag fails in the release workflow without its notes, and only once it is public: check them with the version.
if (-not (Test-Path (Join-Path $root "docs\releases\$($plugin.version).md"))) {
    $failures += "docs\releases\$($plugin.version).md is missing: the release workflow publishes it as v$($plugin.version)'s notes"
}
$release = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'release.ps1'), [ref]$null, [ref]$null)
$default = $release.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Version' } |
    ForEach-Object { $_.DefaultValue.Value }
if ($default -ne $plugin.version) {
    $failures += "release.ps1 builds version $default by default, not the plugin's $($plugin.version)"
}

foreach ($skill in Get-ChildItem (Join-Path $root 'plugins\delta\skills') -Directory) {
    $text = Get-Content (Join-Path $skill.FullName 'SKILL.md') -Raw
    if ($text -notmatch "(?s)^---\r?\nname: $($skill.Name)\r?\ndescription: \S") {
        $failures += "skills\$($skill.Name)\SKILL.md: the front matter does not start with name: $($skill.Name) and a description"
    }
}

foreach ($script in Get-ChildItem $root -Recurse -File -Filter '*.ps1' | Where-Object { $_.FullName -notmatch '\\(dist|bin|obj)\\' }) {
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$errors) | Out-Null
    foreach ($e in $errors) { $failures += "$($script.Name):$($e.Extent.StartLineNumber): $($e.Message)" }
}

if ($failures.Count) {
    $failures | ForEach-Object { "CHECK FAIL $_" }
    exit 1
}
"CHECK ok: delta $($plugin.version), $($marketplace.plugins.Count) plugin(s), PowerShell $($PSVersionTable.PSVersion)"
