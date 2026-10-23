# Builds Delta for Windows x64 and packs dist\delta-<version>-win-x64.zip: the brain, the scene (self-contained, so
# that no .NET install is needed), the launcher, and the licence and notice texts the binaries come with
# (THIRD_PARTY.md). Needs the .NET 10 SDK and Go.
param([string]$Version = '0.5.0')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$name = "delta-$Version-win-x64"
$out = Join-Path $root "dist\$name"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force (Join-Path $out 'LICENSES') | Out-Null

Push-Location (Join-Path $root 'brain')
try {
    $env:CGO_ENABLED = '0'
    go build -trimpath -ldflags "-s -w -buildid= -X main.version=$Version" -o (Join-Path $out 'brain.exe') .
    if ($LASTEXITCODE -ne 0) { throw 'go build failed' }
    $goLicence = Join-Path (go env GOROOT) 'LICENSE'
} finally {
    Pop-Location
}

Push-Location (Join-Path $root 'scene')
try {
    dotnet publish -c Release -r win-x64 --self-contained -p:DisableTransitiveFrameworkReferenceDownloads=true -p:DebugType=none -o (Join-Path $out 'scene') -nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
} finally {
    Pop-Location
}

Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'THIRD_PARTY.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'NOTICE.txt'), (Join-Path $root 'start-delta.ps1') $out
New-Item -ItemType Directory -Force (Join-Path $out 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs\delta.png') (Join-Path $out 'docs')

# Each licence or notice text once, from every package the scene was built from, the .NET runtime pack and Go.
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$lock = (Get-Content (Join-Path $root 'scene\packages.lock.json') -Raw | ConvertFrom-Json).dependencies.'net10.0'
$sources = @($lock.PSObject.Properties | ForEach-Object { @{ Id = $_.Name; Version = $_.Value.resolved } })
$runtime = (Get-Content (Join-Path $out 'scene\scene.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks[0].version
$sources += @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Version = $runtime }
$seen = @{}
$index = @()
foreach ($source in $sources) {
    $dir = Join-Path $packages ('{0}\{1}' -f $source.Id.ToLowerInvariant(), $source.Version)
    $texts = @(Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -match 'licen|notice|copying' -and $_.Extension -match '^(\.txt|\.md|)$' })
    if ($texts.Count -eq 0) { throw "$($source.Id) $($source.Version) has no licence file in $dir" }
    foreach ($text in $texts) {
        $hash = (Get-FileHash $text.FullName).Hash
        if (-not $seen.ContainsKey($hash)) {
            $to = Join-Path $out "LICENSES\$($source.Id)"
            New-Item -ItemType Directory -Force $to | Out-Null
            Copy-Item $text.FullName $to
            $seen[$hash] = "$($source.Id)\$($text.Name)"
        }
        $index += '{0} {1}: LICENSES\{2}' -f $source.Id, $source.Version, $seen[$hash]
    }
}
New-Item -ItemType Directory -Force (Join-Path $out 'LICENSES\Go') | Out-Null
Copy-Item $goLicence (Join-Path $out 'LICENSES\Go\LICENSE')
$index += "Go $(go env GOVERSION) (brain.exe): LICENSES\Go\LICENSE"
$index | Set-Content -Encoding utf8 (Join-Path $out 'LICENSES\PACKAGES.txt')

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $out -DestinationPath $zip
$files = Get-ChildItem $out -Recurse -File
'{0}: {1} files, {2:N1} MB unpacked, {3:N1} MB zipped, SHA-256 {4}' -f $zip, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), ((Get-Item $zip).Length / 1MB), (Get-FileHash $zip).Hash.ToLowerInvariant()
