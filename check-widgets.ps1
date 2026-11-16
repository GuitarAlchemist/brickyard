# The widget system's and the agents' office's CPU checks (widget-tests), on the office module built from this checkout
# twice, as v0.1.0 and v0.2.0 for its reload test, into a folder of its own that is removed after. widget-tests also
# compiles some of scene/'s files, so the pre-commit hook runs this for scene/ too.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$module = Join-Path $root 'modules/AgentOffice/AgentOffice.Module.csproj'
$tests = Join-Path $root 'widget-tests/Widgets.SelfTest.csproj'
$images = Join-Path ([IO.Path]::GetTempPath()) ('delta-widgets-' + [Guid]::NewGuid().ToString('N'))

# These projects use the installed SDK only. A local directory as the sole package source forbids network restore.
foreach ($project in @($module, $tests)) {
    & dotnet restore $project --source $root -p:NuGetAudit=false --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

try {
    foreach ($version in '0.1.0', '0.2.0') {
        & dotnet build $module -c Release --no-restore --nologo -v:q "-p:Version=$version" -o (Join-Path $images $version)
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    & dotnet build $tests -c Release --no-restore --nologo -v:q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $exe = Join-Path $root 'widget-tests/bin/Release/net10.0/Widgets.SelfTest.exe'
    & $exe --wall
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $exe (Join-Path $images '0.1.0/AgentOffice.Module.dll') (Join-Path $images '0.2.0/AgentOffice.Module.dll')
    exit $LASTEXITCODE
}
finally {
    Remove-Item -Recurse -Force $images -ErrorAction SilentlyContinue
}
