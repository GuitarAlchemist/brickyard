# Screenshots of pages in a throwaway headless Chromium, driven over CDP on a local port.
# Usage: shoot.ps1 -Out <dir> -Pages @{name='url'; ...} [-Wait 12] [-Width 1280 -Height 960]
param([string]$Out, [hashtable]$Pages, [int]$Wait = 12, [int]$Width = 1280, [int]$Height = 960, [int]$Port = 9339)
$ErrorActionPreference = 'Stop'
$exe = "$env:LOCALAPPDATA\ms-playwright\chromium-1243\chrome-win64\chrome.exe"
$profileDir = Join-Path $Out ("profile-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force $Out, $profileDir | Out-Null
$chromeArgs = @('--headless=new', '--mute-audio', '--deny-permission-prompts', '--no-first-run', '--hide-scrollbars',
    "--user-data-dir=$profileDir", "--window-size=$Width,$Height", "--remote-debugging-port=$Port",
    '--remote-allow-origins=http://127.0.0.1', '--ignore-gpu-blocklist', '--enable-unsafe-swiftshader', 'about:blank')
$chrome = Start-Process $exe -ArgumentList $chromeArgs -PassThru -WindowStyle Hidden
try {
    $target = $null
    for ($i = 0; $i -lt 40 -and -not $target; $i++) {
        Start-Sleep -Milliseconds 250
        try { $target = (Invoke-RestMethod "http://127.0.0.1:$Port/json" -TimeoutSec 2) | Where-Object type -eq 'page' | Select-Object -First 1 } catch { }
    }
    if (-not $target) { throw 'no page target' }
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.Options.SetRequestHeader('Origin', 'http://127.0.0.1')
    $ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:id = 0
    function Send($method, $params) {
        $script:id++
        $msg = @{ id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 6 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
        $ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).Wait()
        $want = $script:id
        $buf = [byte[]]::new(1MB)
        while ($true) {
            $ms = [IO.MemoryStream]::new()
            do {
                $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).Result
                $ms.Write($buf, 0, $r.Count)
            } while (-not $r.EndOfMessage)
            $reply = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
            if ($reply.id -eq $want) { return $reply }
        }
    }
    Send 'Page.enable' @{} | Out-Null
    foreach ($name in $Pages.Keys) {
        Send 'Page.navigate' @{ url = $Pages[$name] } | Out-Null
        Start-Sleep -Seconds $Wait
        # The page's picture: after the #anchor, the first video, image or canvas; else the largest one.
        $js = @'
(() => {
  const media = [...document.querySelectorAll('video, img, canvas')].filter(e => { const r = e.getBoundingClientRect(); return r.width >= 200 && r.height >= 120; });
  let pick = null;
  const h = decodeURIComponent(location.hash.slice(1));
  const start = h && document.getElementById(h);
  if (start) pick = media.find(e => start.compareDocumentPosition(e) & Node.DOCUMENT_POSITION_FOLLOWING);
  if (!pick) pick = media.sort((a, b) => { const p = a.getBoundingClientRect(), q = b.getBoundingClientRect(); return q.width * q.height - p.width * p.height; })[0];
  if (!pick) return null;
  pick.scrollIntoView({ block: 'center' });
  const r = pick.getBoundingClientRect();
  return JSON.stringify({ x: r.left + scrollX, y: r.top + scrollY, width: r.width, height: r.height, tag: pick.tagName });
})()
'@
        $found = (Send 'Runtime.evaluate' @{ expression = $js; returnByValue = $true }).result.result.value
        Start-Sleep -Seconds 2
        $params = @{ format = 'png' }
        if ($found) {
            $c = $found | ConvertFrom-Json
            "  $name : $($c.tag) $([int]$c.width)x$([int]$c.height)"
            $params = @{ format = 'png'; captureBeyondViewport = $true; clip = @{ x = $c.x; y = $c.y; width = $c.width; height = $c.height; scale = 1 } }
        }
        $shot = Send 'Page.captureScreenshot' $params
        if ($shot.result.data) {
            [IO.File]::WriteAllBytes((Join-Path $Out "$name.png"), [Convert]::FromBase64String($shot.result.data))
            "shot $name"
        } else { "no shot $name : $($shot.error.message)" }
    }
    $ws.Dispose()
} finally {
    Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*$profileDir*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
