# Only uses apps it starts itself in an isolated profile (never the user's own windows).
# Real-app test: Player A keeps TargetA in front and types; Player B uses a real app
# (an Edge window with a throwaway profile) through DuoSeat. Saves a screenshot for checking.
param([ValidateSet('edge')] [string]$App = 'edge', [string]$Shot)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class R {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, IntPtr x);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  public struct POINT { public int X, Y; }
}
"@
[R]::SetProcessDPIAware() | Out-Null
$N = [NullString]::Value
function Wait-Window($cls, $title) { for ($i = 0; $i -lt 60; $i++) { $h = [R]::FindWindow($cls, $title); if ($h -ne [IntPtr]::Zero) { return $h }; Start-Sleep -Milliseconds 250 }; throw "window not found: $cls $title" }

Get-Process DuoSeat, TestTarget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process "$root\dist\DuoSeat.exe" -ArgumentList '--pin','1234' | Out-Null
Start-Sleep -Seconds 2

# Never point this at the user's own apps (Win11 Notepad, for one, reuses an open window).
if ($true) {
  $prof = Join-Path $env:TEMP 'duoseat-edge-test'
  $html = Join-Path $PSScriptRoot 'page.html'
  '<title>DuoSeatTest</title><body style="font:20px sans-serif"><textarea id=t style="width:90%;height:200px;font-size:22px"></textarea><p><button onclick="document.body.style.background=''#9f9''">Click me (turns green)</button></body>' | Set-Content $html
  Start-Process msedge.exe -ArgumentList "--user-data-dir=$prof", '--no-first-run', '--new-window', "file:///$($html.Replace('\','/'))"
  $hb = $null
  for ($i = 0; $i -lt 80 -and -not $hb; $i++) {
    Start-Sleep -Milliseconds 250
    $p = Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'DuoSeatTest*' } | Select-Object -First 1
    if ($p) { $hb = $p.MainWindowHandle }
  }
  if (-not $hb) { throw "edge window not found" }
}
[R]::SetWindowPos($hb, [IntPtr]::Zero, 900, 120, 900, 600, 0x14) | Out-Null   # NOZORDER|NOACTIVATE

Start-Process "$root\test\TestTarget.exe" -ArgumentList 'TargetA','40','120','800','450' | Out-Null
$ha = Wait-Window $N 'TargetA'
for ($i = 0; $i -lt 30 -and [R]::GetForegroundWindow() -ne $ha; $i++) {
  [R]::keybd_event(0x12,0,0,[IntPtr]::Zero); [R]::keybd_event(0x12,0,2,[IntPtr]::Zero); [R]::SetForegroundWindow($ha) | Out-Null
  Start-Sleep -Milliseconds 200
}
if ([R]::GetForegroundWindow() -ne $ha) { throw "could not put TargetA in front" }
$p0 = New-Object R+POINT; [R]::GetCursorPos([ref]$p0) | Out-Null

  $script = @"
sleep 300
goto 1100 300
click
type Hello from B inside Edge! Shift TEST: AbC-dEf_GhI (ok)
sleep 200
vk 09
vk 0D
sleep 400
"@
$script | Set-Content "$PSScriptRoot\script-apps.txt"
$drv = Start-Process "$root\test\TestDriver.exe" -ArgumentList 'localhost','1234','-1','-1',"$PSScriptRoot\script-apps.txt" -PassThru -WindowStyle Hidden
$typed = 0
while (-not $drv.HasExited) {
  if ([R]::GetForegroundWindow() -ne $ha) { Write-Output "!! A lost the foreground"; break }
  [System.Windows.Forms.SendKeys]::SendWait('a'); $typed++
  Start-Sleep -Milliseconds 20
}
$drv.WaitForExit(); Start-Sleep -Milliseconds 700
$p1 = New-Object R+POINT; [R]::GetCursorPos([ref]$p1) | Out-Null
Write-Output "A typed $typed keys; A still in front: $([R]::GetForegroundWindow() -eq $ha); A cursor unchanged: $(($p0.X -eq $p1.X) -and ($p0.Y -eq $p1.Y))"

$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
$g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [Drawing.Point]::Empty, $b.Size)
$bmp.Save($Shot); Write-Output "screenshot: $Shot"


