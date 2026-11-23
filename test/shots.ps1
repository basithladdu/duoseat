# Captures DuoSeat's three windows into docs/ (for the README).
$root = Split-Path $PSScriptRoot
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Drawing; using System.Runtime.InteropServices;
public static class S {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  public struct RECT { public int L, T, R, B; }
  public static void Shot(IntPtr h, string path) {
    RECT w; DwmGetWindowAttribute(h, 9, out w, 16);
    RECT o; GetWindowRect(h, out o);
    using (var bmp = new Bitmap(o.R - o.L, o.B - o.T)) {
      using (var g = Graphics.FromImage(bmp)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
      var crop = new Rectangle(w.L - o.L, w.T - o.T, w.R - w.L, w.B - w.T);
      using (var c = bmp.Clone(crop, bmp.PixelFormat)) c.Save(path);
    }
  }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
"@ -ReferencedAssemblies System.Drawing
[S]::SetProcessDPIAware() | Out-Null
$docs = "$root\docs"; New-Item -ItemType Directory -Force $docs | Out-Null
$N = [NullString]::Value
function Grab($title, $file) {
  for ($i = 0; $i -lt 40; $i++) { $h = [S]::FindWindow($N, $title); if ($h -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 250 }
  Start-Sleep -Milliseconds 800
  [S]::Shot($h, "$docs\$file"); "saved $file"
}
Get-Process DuoSeat -ErrorAction SilentlyContinue | Stop-Process -Force
$p = Start-Process "$root\dist\DuoSeat.exe" -PassThru; Grab 'DuoSeat' 'choose.png'; $p | Stop-Process
$p = Start-Process "$root\dist\DuoSeat.exe" -ArgumentList '--host','--no-admin' -PassThru; Grab 'DuoSeat - Player A' 'player-a.png'; $p | Stop-Process
$p = Start-Process "$root\dist\DuoSeat.exe" -ArgumentList '--client' -PassThru; Grab 'DuoSeat - Player B' 'player-b.png'; $p | Stop-Process
