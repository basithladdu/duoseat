# Note: SendKeys' first key is lost even with no Player B at all (control run), so A may be 1 short.
# Parallel test: "Player A" types into TargetA with the REAL keyboard focus while
# "Player B" (TestDriver over the network) clicks + types into TargetB at the same time.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class T {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, IntPtr x);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, StringBuilder l);
  public struct POINT { public int X, Y; }
  public static string Text(IntPtr top) {
    IntPtr e = GetWindow(top, 5); var sb = new StringBuilder(4096);
    SendMessage(e, 0x0D, (IntPtr)4096, sb); return sb.ToString();
  }
}
"@
[T]::SetProcessDPIAware() | Out-Null

Get-Process DuoSeat, TestTarget -ErrorAction SilentlyContinue | Stop-Process -Force
$host_ = Start-Process "$root\dist\DuoSeat.exe" -ArgumentList '--pin','1234','--no-admin' -PassThru
Start-Sleep -Seconds 2
# TargetB goes on B's screen: the TV if one is plugged in, otherwise the right half of the laptop screen
$tv = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
$bx = if ($tv) { $tv.Bounds.X + 60 } else { 980 }
$b = Start-Process "$root\test\TestTarget.exe" -ArgumentList 'TargetB',"$bx",'150','800','450' -PassThru
Start-Sleep -Milliseconds 800
$a = Start-Process "$root\test\TestTarget.exe" -ArgumentList 'TargetA','60','150','800','450' -PassThru
Start-Sleep -Milliseconds 1500
for ($i = 0; $i -lt 50; $i++) {
  $ha = [T]::FindWindow([NullString]::Value, 'TargetA'); $hb = [T]::FindWindow([NullString]::Value, 'TargetB')
  if ($ha -ne [IntPtr]::Zero -and $hb -ne [IntPtr]::Zero) { break }
  Start-Sleep -Milliseconds 200
}
[T]::keybd_event(0x12,0,0,[IntPtr]::Zero); [T]::keybd_event(0x12,0,2,[IntPtr]::Zero); [T]::SetForegroundWindow($ha) | Out-Null
for ($i = 0; $i -lt 30 -and [T]::GetForegroundWindow() -ne $ha; $i++) {
  Start-Sleep -Milliseconds 200
  [T]::keybd_event(0x12,0,0,[IntPtr]::Zero); [T]::keybd_event(0x12,0,2,[IntPtr]::Zero); [T]::SetForegroundWindow($ha) | Out-Null
}
Start-Sleep -Milliseconds 300
Write-Output ("fg=" + [T]::GetForegroundWindow() + " ha=" + $ha + " hb=" + $hb)
if ([T]::GetForegroundWindow() -ne $ha) { Write-Output "TargetA is not in front - aborting"; exit 1 }
$p0 = New-Object T+POINT; [T]::GetCursorPos([ref]$p0) | Out-Null

@"
sleep 300
gotowin TargetB 300 200
click
type hello from B, typed at the same time as A.
vk 0D
type Second line by B!
sleep 200
ctrl 41
type REPLACED-BY-B (Ctrl+A worked)
"@ | Set-Content "$PSScriptRoot\script-b.txt"

if ($env:NO_B) { $drv = Start-Process powershell -ArgumentList '-Command','Start-Sleep 3' -PassThru -WindowStyle Hidden } else { $drv = Start-Process "$root\test\TestDriver.exe" -ArgumentList 'localhost','1234','-1','-1',"$PSScriptRoot\script-b.txt" -PassThru -NoNewWindow -RedirectStandardOutput "$PSScriptRoot\driver.log" }
# Player A types with the real keyboard, in parallel
$typedA = 0
while (-not $drv.HasExited) {
  if ([T]::GetForegroundWindow() -ne $ha) { Write-Output "!! foreground moved away from TargetA"; break }
  [System.Windows.Forms.SendKeys]::SendWait('a'); $typedA++
  Start-Sleep -Milliseconds 15
}
$drv.WaitForExit()
Start-Sleep -Milliseconds 500
$p1 = New-Object T+POINT; [T]::GetCursorPos([ref]$p1) | Out-Null

$ta = [T]::Text($ha); $tb = [T]::Text($hb)
Write-Output "A typed $typedA keys while B worked"
Write-Output "TargetA text: [$ta]"
Write-Output "TargetB text: [$tb]"
Write-Output ("A's foreground still TargetA: " + ([T]::GetForegroundWindow() -eq $ha))
Write-Output ("A's cursor before $($p0.X),$($p0.Y) after $($p1.X),$($p1.Y)")
$ok = ($ta -match '^a+$') -and ($ta.Length -ge $typedA - 1) -and ($tb -eq 'REPLACED-BY-B (Ctrl+A worked)') -and ($p0.X -eq $p1.X) -and ($p0.Y -eq $p1.Y)
Write-Output ("checks: onlyA=" + ($ta -match '^a+$') + " lenA=" + $ta.Length + "/" + $typedA + " B=" + ($tb -eq 'REPLACED-BY-B (Ctrl+A worked)') + " cursor=" + (($p0.X -eq $p1.X) -and ($p0.Y -eq $p1.Y)))
Write-Output ("RESULT: " + $(if ($ok) { 'PASS' } else { 'FAIL' }))
Get-Content "$PSScriptRoot\driver.log" | Select-Object -First 3







