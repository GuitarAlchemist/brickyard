# What a scene module image says of where it comes from, read as bytes: the module is neither loaded nor run, so an
# unreviewed image can be inspected. Prints its version and informational version (+ the commit it was built at), its
# references, its Source Link, each source file's SHA256 in its PDB, the contracts' MVID it was compiled against, and
# whether the DLL's PdbChecksum matches the PDB beside it. Needs PowerShell 7 (System.Reflection.Metadata).
#
#   pwsh .\inspect-module.ps1 <AgentOffice.Module.dll> [<a contracts DLL, to compare its MVID>]
param(
    [Parameter(Mandatory = $true)][string]$Dll,
    [string]$Contracts
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'inspect-module.ps1 needs PowerShell 7 (pwsh).' }
Add-Type -AssemblyName System.Reflection.Metadata
$Dll = (Resolve-Path $Dll).Path
$pdbPath = [IO.Path]::ChangeExtension($Dll, '.pdb')

function Mvid([string]$path) {
    $fs = [IO.File]::OpenRead($path)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
        $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $md.GetGuid($md.GetModuleDefinition().Mvid)
    } finally { $fs.Dispose() }
}

"sha256: $((Get-FileHash $Dll).Hash)"
$fs = [IO.File]::OpenRead($Dll)
try {
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
    $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $assembly = $md.GetAssemblyDefinition()
    "assembly: $($md.GetString($assembly.Name)) $($assembly.Version), mvid $($md.GetGuid($md.GetModuleDefinition().Mvid))"
    foreach ($h in $md.CustomAttributes) {
        $a = $md.GetCustomAttribute($h)
        if ($a.Constructor.Kind -ne 'MemberReference') { continue }
        $name = $md.GetString($md.GetTypeReference($md.GetMemberReference($a.Constructor).Parent).Name)
        if ($name -in 'AssemblyInformationalVersionAttribute', 'AssemblyFileVersionAttribute', 'AssemblyConfigurationAttribute') {
            $blob = $md.GetBlobReader($a.Value)
            [void]$blob.ReadUInt16()
            "$($name -replace 'Assembly|Attribute', ''): $($blob.ReadSerializedString())"
        }
    }
    foreach ($h in $md.AssemblyReferences) {
        $r = $md.GetAssemblyReference($h)
        "reference: $($md.GetString($r.Name)) $($r.Version)"
    }
    $codeView = $null
    $checksum = $null
    foreach ($entry in $pe.ReadDebugDirectory()) {
        if ($entry.Type -eq 'CodeView') { $codeView = $pe.ReadCodeViewDebugDirectoryData($entry) }
        elseif ($entry.Type -eq 'PdbChecksum') { $checksum = $pe.ReadPdbChecksumDebugDirectoryData($entry) }
    }
} finally { $fs.Dispose() }

if (-not (Test-Path $pdbPath)) {
    "pdb: none beside the DLL"
    return
}

# The PDB's SHA256 with its 20-byte ID zeroed is what the DLL's PdbChecksum records (portable PDB's rule).
$bytes = [IO.File]::ReadAllBytes($pdbPath)
$id = $codeView.Guid.ToByteArray()
$at = -1
for ($i = 0; $i -le $bytes.Length - 20 -and $at -lt 0; $i++) {
    $match = $true
    for ($k = 0; $k -lt 16; $k++) { if ($bytes[$i + $k] -ne $id[$k]) { $match = $false; break } }
    if ($match) { $at = $i }
}
$zeroed = [byte[]]$bytes.Clone()
for ($k = 0; $k -lt 20 -and $at -ge 0; $k++) { $zeroed[$at + $k] = 0 }
$computed = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($zeroed))
$recorded = if ($checksum) { [Convert]::ToHexString([byte[]]$checksum.Checksum) } else { '' }
"pdb: sha256 $((Get-FileHash $pdbPath).Hash); PdbChecksum $(if ($computed -eq $recorded) { 'matches' } else { "DIFFERS: recorded $recorded, computed $computed" })"

$pfs = [IO.File]::OpenRead($pdbPath)
try {
    $p = [System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($pfs).GetMetadataReader()
    foreach ($h in $p.Documents) {
        $d = $p.GetDocument($h)
        "source: $([Convert]::ToHexString($p.GetBlobBytes($d.Hash))) $($p.GetString($d.Name))"
    }
    $sourceLink = [Guid]'CC110556-A091-4D38-9FEC-25AB9A351A6A'
    $references = [Guid]'7E4D4708-096E-4C5C-AEDA-CB10BA6A740D'
    foreach ($h in $p.CustomDebugInformation) {
        $c = $p.GetCustomDebugInformation($h)
        $kind = $p.GetGuid($c.Kind)
        $blob = $p.GetBlobBytes($c.Value)
        if ($kind -eq $sourceLink) { "sourcelink: $([Text.Encoding]::UTF8.GetString($blob))" }
        elseif ($kind -eq $references) {
            # Each compilation reference: its name, NUL, its aliases, NUL, flags (1), time stamp (4), image size (4), MVID (16).
            $i = 0
            while ($i -lt $blob.Length) {
                $end = [Array]::IndexOf($blob, [byte]0, $i)
                $name = [Text.Encoding]::UTF8.GetString($blob, $i, $end - $i)
                $i = [Array]::IndexOf($blob, [byte]0, $end + 1) + 1 + 9
                $mvid = [Guid]::new([byte[]]$blob[$i..($i + 15)])
                $i += 16
                if ($name -like '*Contracts*') { "compiled against: $name mvid $mvid" }
            }
        }
    }
} finally { $pfs.Dispose() }

if ($Contracts) { "contracts given: mvid $(Mvid (Resolve-Path $Contracts).Path)" }
