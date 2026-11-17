# "Own clipboard for B": B copies + pastes with Ctrl+C / Ctrl+V; A's clipboard must stay A's.
# Saves and restores whatever text was on the clipboard; skips if an image/files are on it.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms
if ([System.Windows.Forms.Clipboard]::ContainsImage() -or [System.Windows.Forms.Clipboard]::ContainsFileDropList()) { "SKIPPED: clipboard holds an image/files, not touching it"; exit 0 }
$saved = [System.Windows.Forms.Clipboard]::GetText()
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class C { [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, StringBuilder l);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);
public static string Text(IntPtr top){ IntPtr e=GetWindow(top,5); var sb=new StringBuilder(4096); SendMessage(e,0x0D,(IntPtr)4096,sb); return sb.ToString(); }
public static void SetText(IntPtr top, string s){ IntPtr e=GetWindow(top,5); SendMessage(e,0x0C,IntPtr.Zero,s); } }
"@
try {
  Get-Process DuoSeat, TestTarget -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
  Start-Process "$root\dist\DuoSeat.exe" -ArgumentList '--host', '--pin', '1234', '--own-clipboard' | Out-Null
  Start-Sleep 2
  Start-Process "$root\test\TestTarget.exe" -ArgumentList 'TargetB', '1300', '560', '560', '300', 'top' | Out-Null
  $hb = [IntPtr]::Zero; for ($i = 0; $i -lt 40 -and $hb -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $hb = [C]::FindWindow([NullString]::Value, 'TargetB') }
  [C]::SetText($hb, 'B-ONLY')
  [System.Windows.Forms.Clipboard]::SetText('A-CLIP')
@"
sleep 300
gotowin TargetB 200 120
click
ctrl 41
sleep 200
ctrl 43
sleep 400
vk 23
type +
ctrl 56
sleep 600
"@ | Set-Content "$PSScriptRoot\script-clip.txt"
  & "$root\test\TestDriver.exe" localhost 1234 -1 -1 "$PSScriptRoot\script-clip.txt" | Out-Null
  Start-Sleep -Milliseconds 800
  $a = [System.Windows.Forms.Clipboard]::GetText(); $t = [C]::Text($hb)
  "A's clipboard after B copied + pasted: [$a]   (should be A-CLIP)"
  "B's window: [$t]   (should be B-ONLY+B-ONLY)"
  "RESULT: " + $(if ($a -eq 'A-CLIP' -and $t -eq 'B-ONLY+B-ONLY') { 'PASS' } else { 'FAIL' })
}
finally {
  if ($saved) { [System.Windows.Forms.Clipboard]::SetText($saved) } else { [System.Windows.Forms.Clipboard]::Clear() }
  Get-Process DuoSeat, TestTarget -ErrorAction SilentlyContinue | Stop-Process -Force
}
