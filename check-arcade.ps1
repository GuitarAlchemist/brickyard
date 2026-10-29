param([string]$PreviewDirectory)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$core = Join-Path $root 'modules/ArcadePong/ArcadePong.Core.csproj'
$tests = Join-Path $root 'arcade-tests/ArcadePong.SelfTest.csproj'

# These projects use the installed SDK only. A local directory as the sole package source forbids network restore.
foreach ($project in @($core, $tests)) {
    & dotnet restore $project --source $root -p:NuGetAudit=false --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& dotnet build $core -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($PreviewDirectory) {
    & dotnet run --project $tests -c Release --no-restore --no-launch-profile -- --preview $PreviewDirectory
} else {
    & dotnet run --project $tests -c Release --no-restore --no-launch-profile
}
exit $LASTEXITCODE
