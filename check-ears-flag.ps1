# Says whether each running whisper-server was started with --request-path, the path its routes sit under, which only
# the brain knows (brain/ears.go, earsPath). Reads the processes' command lines from Windows; never prints the path,
# since a web page that learnt it could reach /inference and /load. Run it after a deploy, once delta is reconnected.
#
#   .\check-ears-flag.ps1               every whisper-server running: exit 0 if each has the flag, 1 if one lacks it, 2 if none runs
#   .\check-ears-flag.ps1 -SelfTest     checks the verdicts on made-up command lines, and that none prints the path
param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'

# Verdict on one command line: one of three fixed strings, so that nothing of the line, the path above all, is printed.
function Get-EarsFlagVerdict([string]$CommandLine) {
    if ($CommandLine -notmatch '(^|\s)--request-path(\s|$)') { return '--request-path absent' }
    if ($CommandLine -match '(^|\s)--request-path\s+"?/[0-9a-f]{32}"?(\s|$)') { return '--request-path present (32 hex digits)' }
    return '--request-path present but not 32 hex digits'
}

if ($SelfTest) {
    $hex = '0123456789abcdef0123456789abcdef'
    $exe = '"C:\w\whisper-server.exe" -m "C:\m\ggml.bin" -l fr -t 16 --host 127.0.0.1 --port 56153 --prompt "Delta" -nf -ac 768'
    $cases = @(
        @{ line = "$exe --request-path /$hex"; want = '--request-path present (32 hex digits)' },
        @{ line = "$exe --request-path `"/$hex`""; want = '--request-path present (32 hex digits)' },
        @{ line = $exe; want = '--request-path absent' },
        @{ line = "$exe --request-path /short"; want = '--request-path present but not 32 hex digits' },
        @{ line = "$exe --request-path"; want = '--request-path present but not 32 hex digits' }
    )
    $failed = 0
    foreach ($c in $cases) {
        $got = Get-EarsFlagVerdict $c.line
        if ($got -ne $c.want) { $failed++; Write-Output ("FAIL verdict: got '{0}', want '{1}'" -f ($got -replace $hex, '<hex>' -replace '/short', '<short>'), $c.want) }
        if ($got -match '[0-9a-f]{32}' -or $got -match '/short') { $failed++; Write-Output 'FAIL the verdict prints the path' }
    }
    if ($failed) { Write-Output "SELFTEST failed: $failed"; exit 1 }
    Write-Output "SELFTEST ok: $($cases.Count) command lines, no path printed"
    exit 0
}

$servers = @(Get-CimInstance Win32_Process -Filter "Name='whisper-server.exe'")
if ($servers.Count -eq 0) { Write-Output 'no whisper-server runs'; exit 2 }
$lacking = 0
foreach ($p in $servers) {
    $verdict = Get-EarsFlagVerdict $p.CommandLine
    if ($verdict -ne '--request-path present (32 hex digits)') { $lacking++ }
    Write-Output ("whisper-server pid {0}, child of pid {1}: {2}" -f $p.ProcessId, $p.ParentProcessId, $verdict)
}
if ($lacking) { exit 1 }
exit 0
