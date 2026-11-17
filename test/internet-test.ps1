# Presses "Internet link" on Player A, then connects to the public https link like a far-away browser.
$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class U {
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EP f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  public static string Text(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
  public static List<IntPtr> Kids(IntPtr p) { var r = new List<IntPtr>(); EnumChildWindows(p, (h, l) => { r.Add(h); return true; }, IntPtr.Zero); return r; }
  public static IntPtr Kid(IntPtr p, string t) { foreach (var h in Kids(p)) if (Text(h) == t) return h; return IntPtr.Zero; }
  public static void Click(IntPtr h) { PostMessage(h, 0xF5, IntPtr.Zero, IntPtr.Zero); }
}
"@
$N = [NullString]::Value
$a = [U]::FindWindow($N, 'DuoSeat - Player A')
[U]::Click([U]::Kid($a, 'Internet link'))
$dlg = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $dlg -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $dlg = [U]::FindWindow('#32770', 'DuoSeat') }
[U]::Click([U]::Kid($dlg, 'OK'))
# wait for the link
$link = $null
for ($i = 0; $i -lt 120 -and -not $link; $i++) {
  Start-Sleep -Milliseconds 500
  foreach ($h in [U]::Kids($a)) { if ([U]::Text($h) -match '(https://[a-z0-9-]+\.trycloudflare\.com)') { $link = $Matches[1] } }
}
if (-not $link) { "no link"; exit 1 }
$pin = $null
foreach ($h in [U]::Kids($a)) { if ([U]::Text($h) -match '^\d{8}$') { $pin = [U]::Text($h) } }
"link: $link   pin length: $($pin.Length)"

try {
# 1) the page over https
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ok = $false
for ($i = 0; $i -lt 20 -and -not $ok; $i++) { try { $pg = Invoke-WebRequest "$link/" -UseBasicParsing -TimeoutSec 15; $ok = $pg.Content -match 'Player B' } catch { Start-Sleep 3 } }
"page over https: $ok"

# 2) websocket over wss: PIN, then ask for the live picture
function WsSend($ws, $t) { $b = [Text.Encoding]::UTF8.GetBytes($t); $ws.SendAsync((New-Object 'ArraySegment[byte]' -ArgumentList (, $b)), 'Text', $true, [Threading.CancellationToken]::None).Wait() }
function WsRecv($ws) {
  $buf = New-Object byte[] 2000000; $off = 0
  do { $r = $ws.ReceiveAsync((New-Object 'ArraySegment[byte]' -ArgumentList $buf, $off, ($buf.Length - $off)), [Threading.CancellationToken]::None); $r.Wait(20000) | Out-Null; $off += $r.Result.Count } until ($r.Result.EndOfMessage)
  return @{ type = $r.Result.MessageType; len = $off; text = [Text.Encoding]::UTF8.GetString($buf, 0, [Math]::Min($off, 200)) }
}
$ws = New-Object Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([Uri]($link.Replace('https://', 'wss://') + '/ws'), [Threading.CancellationToken]::None).Wait(20000) | Out-Null
"websocket state: $($ws.State)"
WsSend $ws ('p 00000000')            # wrong PIN first
$m = WsRecv $ws; "wrong PIN answer: $($m.text)"
$ws.Dispose()
$ws = New-Object Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([Uri]($link.Replace('https://', 'wss://') + '/ws'), [Threading.CancellationToken]::None).Wait(20000) | Out-Null
WsSend $ws ("p $pin")
$m = WsRecv $ws; "right PIN answer: $($m.text)"
$t0 = Get-Date
WsSend $ws 's 1'
$m = WsRecv $ws; "first live frame over the internet: $($m.type), $([int]($m.len/1KB)) KB, after $([int]((Get-Date) - $t0).TotalMilliseconds) ms"
WsSend $ws 'f'
$m = WsRecv $ws; "second frame: $($m.type), $([int]($m.len/1KB)) KB"
$ws.CloseAsync('NormalClosure', 'bye', [Threading.CancellationToken]::None).Wait(5000) | Out-Null

} finally {
  # close the link again, whatever happened
  [U]::Click([U]::Kid($a, 'Close link'))
  Start-Sleep 2
  "cloudflared still running: " + [bool](Get-Process cloudflared -ErrorAction SilentlyContinue)
}
