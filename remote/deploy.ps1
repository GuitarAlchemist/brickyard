# Builds this checkout's gateway and puts it in place of the one running from -Dir, with the same arguments: paired
# devices, the tunnel's entries and Access stay as they are. The build is <Dir>\delta-remote-<git describe>.exe, built
# as a plain `go build` builds it, so that a commit always gives the same bytes and deploying the running one restarts
# nothing.
#
#   .\remote\deploy.ps1 -Dir <the folder the gateway runs from> [-Check] [-Exe <a build>] [-Back]
#
# -Check builds aside and says what runs, whether anyone is connected, whether the job a new gateway would start in ends
# it when that job closes, and whether a deploy would replace it; it changes nothing. Otherwise nothing is replaced while a device, a guest or Delta's window holds a connection to one of the
# gateway's entries. A new build that does not listen on the old one's ports within 15 s is stopped and the old one
# starts again. -Exe puts that build in place instead of this checkout's; -Back puts back the one replaced last.
# <Dir>\deploy.json keeps the arguments, the replaced build and the one started, so a gateway that died starts again
# with its arguments, and `delta-remote -resume <Dir>` starts that build at logon (resume_windows.go).
# Each start logs to <Dir>\out-<time>.log and err-<time>.log.
param(
    [Parameter(Mandatory = $true)][string]$Dir,
    [string]$Exe,
    [switch]$Back,
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
# Paths are taken literally throughout: a folder may hold [ ], which PowerShell would read as a pattern.
$Dir = (Resolve-Path -LiteralPath $Dir).Path
$statePath = Join-Path $Dir 'deploy.json'
$state = $null
if (Test-Path -LiteralPath $statePath) { $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json }

# Not Get-FileHash: Windows PowerShell started from PowerShell 7 inherits its PSModulePath, and then has none.
function Hash([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $file = [IO.File]::OpenRead($path)
    try { [BitConverter]::ToString($sha.ComputeHash($file)) } finally { $file.Dispose(); $sha.Dispose() }
}

# This folder's gateway is the delta-remote*.exe started from it; one started from elsewhere is not touched. Callers
# wrap these three in @(): Windows PowerShell unrolls a function's one CIM object, whose .Count is then $null.
function Running {
    @(Get-CimInstance Win32_Process -Filter "Name LIKE 'delta-remote%.exe'" |
        Where-Object { $_.ExecutablePath -and (Split-Path $_.ExecutablePath) -eq $Dir })
}

function Listening([int]$id) {
    @(Get-NetTCPConnection -OwningProcess $id -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { [int]$_.LocalPort } | Sort-Object -Unique)
}

# A device's page and a guest's, through the tunnel, keep /said open while they watch.
function Connected([int]$id) {
    $ports = @(Listening $id)
    @(Get-NetTCPConnection -OwningProcess $id -State Established -ErrorAction SilentlyContinue |
        Where-Object { $ports -contains [int]$_.LocalPort })
}

function Save($arguments, $previous, $current) {
    $json = [pscustomobject]@{ arguments = $arguments; previous = $previous; current = $current } | ConvertTo-Json
    [IO.File]::WriteAllText($statePath, $json + "`n", (New-Object Text.UTF8Encoding $false))
}

# CreateProcess with the command line as it is, so that no shell reads the path or the arguments, and with the two
# log files as the only handles the gateway inherits: Start-Process's redirection hands down this script's own output
# handles too, and a shell or a tool reading them through a pipe would wait until the gateway ends.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public class DeployedGateway : IDisposable {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo {
        public int cb; public string reserved, desktop, title;
        public int x, y, xSize, ySize, xChars, yChars, fill, flags;
        public short show, reserved2Size; public IntPtr reserved2, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx { public StartupInfo info; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr process, thread; public int id, threadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, int flags, IntPtr environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(IntPtr h, int mask, int flags);
    [DllImport("kernel32.dll")] static extern int WaitForSingleObject(IntPtr h, int milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length, IntPtr returned);

    const int HandleList = 0x20002, NoWindow = 0x08000000, ExtendedStartup = 0x80000, UseStdHandles = 0x100,
        InsufficientBuffer = 122, ExtendedLimits = 9, KillOnJobClose = 0x2000;

    // The job a gateway started now lands in: this script's own, since it does not break away. Only the immediate job
    // is read; the jobs it may be nested in are not.
    public static string Job() {
        bool inJob;
        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob)) throw new Win32Exception();
        if (!inJob) return "none";
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION: LimitFlags sits after two 8-byte times on both architectures.
        int size = IntPtr.Size == 8 ? 144 : 112;
        IntPtr info = Marshal.AllocHGlobal(size);
        try {
            if (!QueryInformationJobObject(IntPtr.Zero, ExtendedLimits, info, size, IntPtr.Zero)) throw new Win32Exception();
            if ((Marshal.ReadInt32(info, 16) & KillOnJobClose) != 0)
                return "this shell's, which ends it when the job closes: deploy from a shell that outlives this one";
            return "this shell's, which does not end it when the job closes (the jobs it is nested in are not read)";
        } finally {
            Marshal.FreeHGlobal(info);
        }
    }
    IntPtr process;
    public int Id;
    public bool HasExited { get { return WaitForSingleObject(process, 0) == 0; } }

    // Closes this script's handle on the gateway; the gateway runs on.
    public void Dispose() {
        if (process != IntPtr.Zero) CloseHandle(process);
        process = IntPtr.Zero;
    }

    public static DeployedGateway Start(string exe, string commandLine, string directory, string outLog, string errLog) {
        using (FileStream output = new FileStream(outLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (FileStream error = new FileStream(errLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) {
            IntPtr o = output.SafeFileHandle.DangerousGetHandle(), e = error.SafeFileHandle.DangerousGetHandle();
            if (!SetHandleInformation(o, 1, 1) || !SetHandleInformation(e, 1, 1)) throw new Win32Exception();
            // Asked for its size only, the list says so by failing with ERROR_INSUFFICIENT_BUFFER.
            IntPtr size = IntPtr.Zero;
            if (!InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size) &&
                Marshal.GetLastWin32Error() != InsufficientBuffer) throw new Win32Exception();
            IntPtr list = IntPtr.Zero, handles = IntPtr.Zero;
            bool initialized = false;
            try {
                list = Marshal.AllocHGlobal(size);
                handles = Marshal.AllocHGlobal(2 * IntPtr.Size);
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
                initialized = true;
                Marshal.WriteIntPtr(handles, 0, o);
                Marshal.WriteIntPtr(handles, IntPtr.Size, e);
                if (!UpdateProcThreadAttribute(list, 0, (IntPtr)HandleList, handles, (IntPtr)(2 * IntPtr.Size),
                    IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                StartupInfoEx startup = new StartupInfoEx();
                startup.info.cb = Marshal.SizeOf(typeof(StartupInfoEx));
                startup.info.flags = UseStdHandles;
                startup.info.output = o;
                startup.info.error = e;
                startup.attributes = list;
                ProcessInformation started;
                if (!CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                    NoWindow | ExtendedStartup, IntPtr.Zero, directory, ref startup, out started)) throw new Win32Exception();
                CloseHandle(started.thread);
                DeployedGateway g = new DeployedGateway();
                g.process = started.process;
                g.Id = started.id;
                return g;
            } finally {
                if (initialized) DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
                Marshal.FreeHGlobal(handles);
            }
        }
    }
}
'@

function Start-Gateway([string]$path, [string]$arguments) {
    $stamp = Get-Date -Format 'MMdd-HHmmss-fff'
    $line = '"' + $path + '"'
    if ($arguments) { $line += ' ' + $arguments }
    # A file Windows will not start (corrupt, another architecture, blocked, gone) is a start that failed: the caller
    # gets $null and the reason in $launchError, and rolls back as for a build that exits at once.
    try { [DeployedGateway]::Start($path, $line, $Dir, (Join-Path $Dir "out-$stamp.log"), (Join-Path $Dir "err-$stamp.log")) }
    catch { $script:launchError = $_.Exception.GetBaseException().Message; $null }
}

# Until it listens on every one of $ports (on one port at least when none is known), it exits, or 15 s pass.
function Await($process, [int[]]$ports) {
    $until = (Get-Date).AddSeconds(15)
    if (-not $process) { return $false }
    while ((Get-Date) -lt $until) {
        if ($process.HasExited) { return $false }
        $now = @(Listening $process.Id)
        if ($now.Count -and -not @($ports | Where-Object { $now -notcontains $_ }).Count) { return $true }
        Start-Sleep -Milliseconds 250
    }
    $false
}

$running = @(Running)
if ($running.Count -gt 1) {
    throw "$($running.Count) gateways run from $Dir (PIDs $(($running | ForEach-Object ProcessId) -join ', ')): stop all but one by hand"
}
$old = $null
if ($running.Count) { $old = $running[0] }

$built = $null
if ($Back) {
    if (-not $state -or -not $state.previous) { throw "$statePath names no replaced build to put back" }
    $target = $state.previous
} elseif ($Exe) {
    $target = (Resolve-Path -LiteralPath $Exe).Path
} else {
    $describe = (& git -C $PSScriptRoot describe --tags --always --dirty).Trim()
    if ($describe -like '*-dirty') { $describe += '-' + (Get-Date -Format 'HHmmss') }
    if ($Check) { $built = Join-Path $env:TEMP "delta-remote-check-$PID.exe" } else { $built = Join-Path $Dir ".building-$PID.exe" }
    Push-Location $PSScriptRoot
    try {
        & go build -o $built .
        if ($LASTEXITCODE -ne 0) { throw 'go build failed' }
    } finally { Pop-Location }
    $target = $built
}
if (-not (Test-Path -LiteralPath $target)) { throw "$target does not exist" }

if ($old) {
    # The arguments are its command line after the executable. Where that cannot be told, nothing is replaced: a
    # command line this script may not read, or an unquoted path with a space, whose first word is not the executable.
    $first = [regex]::Match([string]$old.CommandLine, '^\s*("([^"]*)"|(\S+))')
    $word = $first.Groups[2].Value + $first.Groups[3].Value
    if (-not $old.CommandLine -or -not $first.Success -or
        [IO.Path]::GetFileNameWithoutExtension($word) -ne [IO.Path]::GetFileNameWithoutExtension($old.ExecutablePath)) {
        if ($built) { Remove-Item -LiteralPath $built }
        throw "the arguments of $(Split-Path $old.ExecutablePath -Leaf), PID $($old.ProcessId), cannot be told from its command line: nothing replaced"
    }
    $arguments = $old.CommandLine.Substring($first.Length).TrimStart()
    $ports = @(Listening $old.ProcessId)
    $clients = @(Connected $old.ProcessId)
    $same = (Hash $old.ExecutablePath) -eq (Hash $target)
} else {
    $arguments = $null
    if ($state) { $arguments = $state.arguments }
    $ports = @()
    $clients = @()
    $same = $false
}

if ($Check) {
    if ($old) {
        "running: $(Split-Path $old.ExecutablePath -Leaf), PID $($old.ProcessId), since $($old.CreationDate.ToUniversalTime().ToString('u'))"
        "listening on: $($ports -join ', ')"
        # The flags only: their values name the owner, the Access team and the hosts.
        "arguments: $(([regex]::Matches($arguments, '(?<=^|\s)-[a-z-]+') | ForEach-Object Value) -join ' ')"
        "connected: $($clients.Count)$(if ($clients.Count) { ' on ' + (($clients | ForEach-Object LocalPort | Sort-Object -Unique) -join ', ') })"
    } else {
        "running: no gateway from $Dir$(if ($state) { '; deploy.json keeps its arguments' } else { ', and no deploy.json' })"
    }
    "a new gateway's job: $([DeployedGateway]::Job())"
    if ($same) { $verdict = 'nothing: it already runs this build' }
    elseif (-not $old -and -not $state) { $verdict = 'nothing: no arguments to start it with' }
    elseif ($clients.Count) { $verdict = 'nothing while someone is connected' }
    elseif ($old) { $verdict = "replace it with $(if ($Back -or $Exe) { Split-Path $target -Leaf } else { "delta-remote-$describe.exe" })" }
    else { $verdict = 'start it' }
    "a deploy would do: $verdict"
    if ($built) { Remove-Item -LiteralPath $built }
    return
}

if ($same) {
    if ($built) { Remove-Item -LiteralPath $built }
    "$(Split-Path $old.ExecutablePath -Leaf) already runs this build, PID $($old.ProcessId): nothing replaced"
    return
}
if (-not $old -and -not $state) {
    if ($built) { Remove-Item -LiteralPath $built }
    throw "no gateway runs from $Dir and it has no deploy.json to start one with: start it once by hand, then deploy"
}
if ($clients.Count) {
    if ($built) { Remove-Item -LiteralPath $built }
    throw "$($clients.Count) connection(s) on port(s) $(($clients | ForEach-Object LocalPort | Sort-Object -Unique) -join ', '): nothing replaced"
}
# Only now does a build from the checkout go into the folder: a refused deploy leaves the folder as it was.
if ($built) {
    $target = Join-Path $Dir "delta-remote-$describe.exe"
    Move-Item -Force -LiteralPath $built -Destination $target
}

$previous = $null
if ($old) { $previous = $old.ExecutablePath } elseif ($state) { $previous = $state.previous }
Save $arguments $previous $target
if ($old) {
    Stop-Process -Id $old.ProcessId -Force
    Wait-Process -Id $old.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
}
$name = Split-Path $target -Leaf
$new = Start-Gateway $target $arguments
if (Await $new $ports) {
    "$name runs, PID $($new.Id), listening on $((Listening $new.Id) -join ', ')"
    $new.Dispose()
    return
}
if ($new -and -not $new.HasExited) { Stop-Process -Id $new.Id -Force; Wait-Process -Id $new.Id -Timeout 10 -ErrorAction SilentlyContinue }
if ($new) { $new.Dispose(); $failed = "$name did not listen on $($ports -join ', ') within 15 s" } else { $failed = "$name could not start: $launchError" }
if (-not $old) {
    Save $arguments $previous $state.current
    throw "$failed; no gateway runs"
}
$before = $null
if ($state) { $before = $state.previous }
Save $arguments $before $old.ExecutablePath
$oldName = Split-Path $old.ExecutablePath -Leaf
$again = Start-Gateway $old.ExecutablePath $arguments
$back = Await $again $ports
$starting = $again -and -not $back -and -not $again.HasExited
if ($again) { $again.Dispose() }
if ($back) { throw "$failed; $oldName runs again, PID $($again.Id)" }
# Left running: it may still be starting, and it is the only build there is.
if ($starting) { throw "$failed, and $oldName did not listen again within 15 s: it still runs, PID $($again.Id), and may yet listen" }
if ($again) { throw "$failed, and $oldName did not listen again within 15 s: no gateway runs" }
throw "$failed, and $oldName could not start again: $launchError; no gateway runs"