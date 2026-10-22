# Runs every check CI runs, in its order: the plugin and the scripts, the brain, the scene. CI runs this script, so a
# green run here is a green run there. Needs Go and the .NET 10 SDK on PATH; it names the one missing.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Step([string]$name, [scriptblock]$body) {
    "== $name"
    $global:LASTEXITCODE = 0
    & $body
    if ($LASTEXITCODE -ne 0) {
        "CHECK FAIL $name (exit $LASTEXITCODE)"
        exit 1
    }
}

foreach ($tool in 'go', 'gofmt', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        "CHECK FAIL $tool is not on PATH"
        exit 1
    }
}

# check-plugin.ps1 parses every script in the PowerShell that runs it, and users have Windows PowerShell 5.1.
Step 'plugin, marketplace and PowerShell scripts' {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'check-plugin.ps1')
}

Push-Location (Join-Path $root 'brain')
try {
    Step 'gofmt' {
        $unformatted = gofmt -l .
        if ($unformatted) {
            "gofmt lists: $unformatted (a Windows editor or script may have written CRLF)"
            $global:LASTEXITCODE = 1
        }
    }
    Step 'go vet' { go vet ./... }
    Step 'go test' { go test ./... }
}
finally { Pop-Location }

Push-Location (Join-Path $root 'scene')
try {
    Step 'scene restore' { dotnet restore --locked-mode -r win-x64 }
    Step 'scene build' { dotnet build -c Release --no-restore -nologo }
    Step 'scene selftest' { dotnet run -c Release --no-build -- --selftest }
}
finally { Pop-Location }

'CHECK ok: plugin, brain and scene'
