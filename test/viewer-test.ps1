# Player B's app on the same PC: Live view window -> click + type -> lands in TargetB on "A's" screen.
param([string]$Shot)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class V {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, StringBuilder l);
  public struct RECT { public int L, T, R, B; } public struct POINT { public int X, Y; }
  public static string Text(IntPtr top) { IntPtr e = GetWindow(top, 5); var sb = new StringBuilder(4096); SendMessage(e, 0x0D, (IntPtr)4096, sb); return sb.ToString(); }
}
"@
[V]::SetProcessDPIAware() | Out-Null
$N = [NullString]::Value
function WaitWin($t) { for ($i = 0; $i -lt 60; $i++) { $h = [V]::FindWindow($N, $t); if ($h -ne [IntPtr]::Zero) { return $h }; Start-Sleep -Milliseconds 250 }; throw "no window $t" }

Get-Process DuoSeat, TestTarget -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
Start-Process "$root\dist\DuoSeat.exe" -ArgumentList '--host', '--pin', '1234', '--no-admin' | Out-Null
Start-Sleep -Seconds 2
# B's screen: the TV / extra monitor if there is one, else the laptop screen (physical pixels, this script is DPI aware)
$scr = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
if (-not $scr) { $scr = [System.Windows.Forms.Screen]::PrimaryScreen }
$A = $scr.Bounds
$tx = $A.X + [int]($A.Width * 0.35); $ty = $A.Y + [int]($A.Height * 0.3)
Start-Process "$root\test\TestTarget.exe" -ArgumentList 'TargetB', "$tx", "$ty", '560', '300', 'top' | Out-Null
$hb = WaitWin 'TargetB'
Start-Process "$root\dist\DuoSeat.exe" -ArgumentList '--connect', 'localhost', '--pin', '1234', '--live' | Out-Null
$hv = WaitWin 'DuoSeat - live view (your cursor is the orange one)'
[V]::SetWindowPos($hv, [IntPtr]::Zero, 20, 40, 1200, 720, 0x4) | Out-Null
Start-Sleep -Seconds 3

# where TargetB's text box (280,140 into the window) appears inside the viewer picture
$cr = New-Object V+RECT; [V]::GetClientRect($hv, [ref]$cr) | Out-Null
$s = [Math]::Min($cr.R / $A.Width, $cr.B / $A.Height); $fw = $A.Width * $s; $fh = $A.Height * $s
$fx = ($cr.R - $fw) / 2; $fy = ($cr.B - $fh) / 2
# the text box's real spot on B's screen (TestTarget may be DPI-scaled, so measure instead of guessing)
$er = New-Object V+RECT; [V]::GetWindowRect([V]::GetWindow($hb, 5), [ref]$er) | Out-Null
$ex = ($er.L + $er.R) / 2; $ey = ($er.T + $er.B) / 2
$p = New-Object V+POINT; $p.X = [int]($fx + ($ex - $A.X) * $s); $p.Y = [int]($fy + ($ey - $A.Y) * $s)
[V]::ClientToScreen($hv, [ref]$p) | Out-Null
"click inside viewer at screen $($p.X),$($p.Y)"
[V]::SetCursorPos($p.X, $p.Y) | Out-Null; Start-Sleep -Milliseconds 200
[V]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60; [V]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500
"viewer in front: " + ([V]::GetForegroundWindow() -eq $hv)
[System.Windows.Forms.SendKeys]::SendWait('Typed in the Live view{ENTER}Line 2 ABC-xyz')
Start-Sleep -Milliseconds 800
$t = [V]::Text($hb)
"TargetB text: [" + $t.Replace("`r`n", '<ENTER>') + "]"
"RESULT: " + $(if ($t -eq "Typed in the Live view`r`nLine 2 ABC-xyz") { 'PASS' } else { 'FAIL' })
if ($Shot) {
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
  $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [Drawing.Point]::Empty, $b.Size); $bmp.Save($Shot)
}
