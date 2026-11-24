// Things Player A can switch on: a virtual screen for B, an internet link, admin mode.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DuoSeat
{
    static class Folders
    {
        public static string Data
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuoSeat");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static void Download(string url, string path)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            string tmp = path + ".part";
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "DuoSeat");
                wc.DownloadFile(url, tmp);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static string Sha256(string path)
        {
            using (SHA256 s = SHA256.Create())
            using (FileStream f = File.OpenRead(path))
                return BitConverter.ToString(s.ComputeHash(f)).Replace("-", "");
        }

        // Runs a .cmd elevated (one UAC prompt). Returns false if the person said no.
        public static bool RunElevated(string script, int timeoutMs)
        {
            string cmd = Path.Combine(Data, "step.cmd");
            File.WriteAllText(cmd, "@echo off\r\n" + script + "\r\n", Encoding.Default);
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"")
            { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            try
            {
                using (Process p = Process.Start(psi)) { p.WaitForExit(timeoutMs); }
                return true;
            }
            catch (Win32Exception) { return false; } // UAC cancelled
        }
    }

    // ------------------------------------------------------------------ virtual screen
    // A real extra screen with no cable: the open-source Virtual Display Driver (MIT,
    // github.com/VirtualDrivers/Virtual-Display-Driver). Pinned to the release Microsoft's
    // winget catalogue lists, checked by SHA-256 before anything runs.
    static class VirtualScreen
    {
        const string Url = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VDD.Control.25.7.23.zip";
        const string Sha = "A701F2272E9FCF382849B24F913C6DD07597B3B1116525F2E90182F019609154";
        public const string AdapterName = "Virtual Display Driver";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, uint i, ref DISPLAY_DEVICE dd, uint flags);

        // \\.\DISPLAY3 -> "Virtual Display Driver" / "Intel(R) UHD Graphics" ...
        public static string AdapterOf(string deviceName)
        {
            DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if (string.Equals(dd.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return dd.DeviceString ?? "";
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            }
            return "";
        }

        public static bool IsVirtual(string deviceName)
        {
            string a = AdapterOf(deviceName);
            return a.IndexOf("Virtual Display", StringComparison.OrdinalIgnoreCase) >= 0
                || a.IndexOf("IddSample", StringComparison.OrdinalIgnoreCase) >= 0
                || a.IndexOf("Parsec Virtual", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string Dir { get { return Path.Combine(Folders.Data, "vdd"); } }

        // the release calls its x64 build "x86"; ARM laptops (Snapdragon) need the ARM64 one
        static string Arch
        {
            get
            {
                string a = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "");
                string id = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
                return a.ToUpperInvariant().Contains("ARM") || id.ToUpperInvariant().Contains("ARM") ? "ARM64" : "x86";
            }
        }

        static void Fetch(Action<string> status)
        {
            string zip = Path.Combine(Folders.Data, "vdd.zip");
            if (!File.Exists(zip) || Folders.Sha256(zip) != Sha)
            {
                status("Downloading the virtual screen driver (official GitHub release, 70 MB)...");
                Folders.Download(Url, zip);
            }
            if (Folders.Sha256(zip) != Sha) { File.Delete(zip); throw new Exception("download didn't match the official file, stopped"); }
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            using (ZipArchive z = ZipFile.OpenRead(zip))
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string n = e.FullName.Replace('/', '\\');
                    if (e.Name.Length == 0) continue;   // folder entry
                    if (!(n.StartsWith("Dependencies\\devcon") || n.StartsWith("SignedDrivers\\" + Arch + "\\VDD\\"))) continue;
                    string to = Path.Combine(Dir, n);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    e.ExtractToFile(to, true);
                }
        }

        public static bool Install(Action<string> status)
        {
            Fetch(status);
            status("Windows will ask for permission - click Yes to add the screen...");
            string devcon = Path.Combine(Dir, "Dependencies\\devcon.exe");
            string drv = Path.Combine(Dir, "SignedDrivers\\" + Arch + "\\VDD");
            string script =
                "if not exist \"C:\\VirtualDisplayDriver\" mkdir \"C:\\VirtualDisplayDriver\"\r\n" +
                "if not exist \"C:\\VirtualDisplayDriver\\vdd_settings.xml\" copy /y \"" + drv + "\\vdd_settings.xml\" \"C:\\VirtualDisplayDriver\\\" >nul\r\n" +
                "\"" + devcon + "\" install \"" + drv + "\\MttVDD.inf\" Root\\MttVDD > \"" + Path.Combine(Folders.Data, "vdd-install.log") + "\" 2>&1";
            return Folders.RunElevated(script, 120000);
        }

        public static bool Remove(Action<string> status)
        {
            string devcon = Path.Combine(Dir, "Dependencies\\devcon.exe");
            if (!File.Exists(devcon)) Fetch(status);
            status("Windows will ask for permission - click Yes to remove the screen...");
            return Folders.RunElevated("\"" + devcon + "\" remove Root\\MttVDD > \"" + Path.Combine(Folders.Data, "vdd-remove.log") + "\" 2>&1", 60000);
        }
    }

    // ------------------------------------------------------------------ internet link
    // Cloudflare's free "quick tunnel": an https://*.trycloudflare.com link that reaches
    // this PC's DuoSeat from anywhere, without router settings. cloudflared is Cloudflare's
    // official open-source client; DuoSeat only runs it if Windows says it's signed by Cloudflare.
    class Tunnel
    {
        const string Url = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
        Process proc;
        public string Link;

        public static string ExePath { get { return Path.Combine(Folders.Data, "cloudflared.exe"); } }

        public void Start(Action<string> status, Action<string> onLink, Action<string> onFail)
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    if (!File.Exists(ExePath) || !Signed.ByCloudflare(ExePath))
                    {
                        status("Downloading cloudflared (Cloudflare's official tunnel)...");
                        Folders.Download(Url, ExePath);
                    }
                    if (!Signed.ByCloudflare(ExePath)) { File.Delete(ExePath); onFail("cloudflared wasn't signed by Cloudflare, stopped"); return; }
                    status("Opening the internet link...");
                    ProcessStartInfo psi = new ProcessStartInfo(ExePath,
                        "tunnel --no-autoupdate --url http://localhost:" + Proto.TcpPort)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                    proc = Process.Start(psi);
                    Regex rx = new Regex(@"https://[a-z0-9-]+\.trycloudflare\.com");
                    bool found = false;
                    DataReceivedEventHandler h = delegate (object s, DataReceivedEventArgs e)
                    {
                        if (found || e.Data == null) return;
                        Match m = rx.Match(e.Data);
                        if (m.Success) { found = true; Link = m.Value; onLink(m.Value); }
                    };
                    proc.ErrorDataReceived += h; proc.OutputDataReceived += h;
                    proc.BeginErrorReadLine(); proc.BeginOutputReadLine();
                    if (!proc.WaitForExit(30000) && !found) { } // keeps running
                    if (!found && proc.HasExited) onFail("cloudflared stopped. Check the internet connection.");
                    else if (!found) onFail("No link from Cloudflare yet. Try again in a minute.");
                }
                catch (Exception ex) { onFail(ex.Message); }
            }) { IsBackground = true, Name = "tunnel" };
            t.Start();
        }

        public void Stop()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            proc = null; Link = null;
        }
    }

    static class Signed
    {
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO { public int cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData;
            public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice; public IntPtr pFile;
            public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
            public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
        }

        // Authenticode-valid AND the signer is Cloudflare.
        public static bool ByCloudflare(string path)
        {
            try
            {
                if (!Valid(path)) return false;
                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
                return cert.Subject.IndexOf("Cloudflare", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        static bool Valid(string path)
        {
            WINTRUST_FILE_INFO fi = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = path };
            IntPtr pfi = Marshal.AllocHGlobal(Marshal.SizeOf(fi));
            Marshal.StructureToPtr(fi, pfi, false);
            WINTRUST_DATA wd = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf(typeof(WINTRUST_DATA)), dwUIChoice = 2 /*none*/, fdwRevocationChecks = 0,
                dwUnionChoice = 1 /*file*/, pFile = pfi, dwStateAction = 0, dwProvFlags = 0x10 /*cache only url*/
            };
            IntPtr pwd = Marshal.AllocHGlobal(Marshal.SizeOf(wd));
            Marshal.StructureToPtr(wd, pwd, false);
            try
            {
                Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(new IntPtr(-1), action, pwd) == 0;
            }
            finally { Marshal.FreeHGlobal(pfi); Marshal.FreeHGlobal(pwd); }
        }
    }

    // B's own clipboard. Windows has one; we keep a copy of each side's and swap around B's shortcuts.
    class ClipSwapper : Injector.IClipSwap
    {
        readonly System.Windows.Forms.Form ui;
        public ClipSwapper(System.Windows.Forms.Form ui) { this.ui = ui; }

        public object Take()
        {
            try { return ui.Invoke((Func<object>)Snapshot); } catch { return null; }
        }

        public void Put(object snapshot)
        {
            try { ui.Invoke((Action)delegate { Restore(snapshot); }); } catch { }
        }

        static object Snapshot()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    System.Windows.Forms.IDataObject d = System.Windows.Forms.Clipboard.GetDataObject();
                    if (d == null) return null;
                    System.Windows.Forms.DataObject copy = new System.Windows.Forms.DataObject();
                    int n = 0;
                    foreach (string fmt in d.GetFormats(false))
                    {
                        try
                        {
                            object o = d.GetData(fmt, false);
                            if (o == null) continue;
                            MemoryStream ms = o as MemoryStream;
                            if (ms != null) o = new MemoryStream(ms.ToArray());
                            copy.SetData(fmt, false, o);
                            n++;
                        }
                        catch { }
                    }
                    return n == 0 ? null : copy;
                }
                catch (ExternalException) { Thread.Sleep(30); }   // another app has the clipboard open
            }
            return null;
        }

        static void Restore(object snapshot)
        {
            try
            {
                if (snapshot == null) System.Windows.Forms.Clipboard.Clear();
                else System.Windows.Forms.Clipboard.SetDataObject(snapshot, true, 10, 50);
            }
            catch { }
        }
    }

    // Too many wrong PINs or links: lock the door. Every handshake counts as a wrong try the moment it
    // starts and a right PIN takes it back, so opening lots of connections at once can't test more than
    // 5 guesses. Each lockout is twice as long as the one before (1, 2, 4 ... 60 minutes) until someone gets in.
    static class Guard
    {
        public const int Tries = 5;
        static readonly Queue<DateTime> fails = new Queue<DateTime>();
        static int pending, lockouts;
        static DateTime lockedUntil = DateTime.MinValue;
        static readonly object l = new object();

        public static bool Locked { get { lock (l) return DateTime.UtcNow < lockedUntil; } }

        public static string Message
        {
            get
            {
                lock (l)
                {
                    int min = (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalMinutes);
                    return min > 0 ? "Too many wrong tries - locked for " + min + " min." : "Too many tries at once - wait a moment.";
                }
            }
        }

        static void Trim(DateTime now) { while (fails.Count > 0 && (now - fails.Peek()).TotalSeconds > 60) fails.Dequeue(); }

        // before the handshake: false = locked, or too many tries already in flight
        public static bool TryBegin()
        {
            lock (l)
            {
                DateTime now = DateTime.UtcNow;
                if (now < lockedUntil) return false;
                Trim(now);
                if (fails.Count + pending >= Tries) return false;
                pending++;
                return true;
            }
        }

        // every TryBegin ends in exactly one of these two
        public static void Passed() { lock (l) { if (pending > 0) pending--; lockouts = 0; } }

        public static void Failed()
        {
            lock (l)
            {
                if (pending > 0) pending--;
                DateTime now = DateTime.UtcNow;
                fails.Enqueue(now);
                Trim(now);
                if (fails.Count >= Tries)
                {
                    lockedUntil = now.AddMinutes(Math.Min(60, 1 << Math.Min(lockouts, 6)));
                    lockouts++;
                    fails.Clear();
                }
            }
        }
    }
}
