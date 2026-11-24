// DuoSeat client (Player B's laptop): captures this laptop's mouse + keyboard
// and sends them to Player A's laptop, where they drive Player B's own cursor.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DuoSeat
{
    static class ClientProgram
    {
        public static void Run()
        {
            Application.Run(new ClientForm());
        }
    }

    class CaptureCurtain : Form
    {
        // Touchpads (two-finger scroll) skip the low-level mouse hook: Windows hands the
        // finished scroll straight to the window under the cursor. While B is in control
        // that's this curtain, so it catches the scroll and sends it to Player A.
        public Action<int, bool> Wheel;

        protected override void WndProc(ref Message m)
        {
            if ((m.Msg == W.WM_MOUSEWHEEL || m.Msg == W.WM_MOUSEHWHEEL) && Wheel != null)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (delta != 0) Wheel(delta, m.Msg == W.WM_MOUSEHWHEEL);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        public CaptureCurtain()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(10, 10, 20);
            Opacity = 0.72;
            Label l = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 22f, FontStyle.Bold),
                Text = "Your mouse and keyboard are now on Player A's screen\n(look for the orange B cursor)\n\n"
                     + "Ctrl + Alt + X   = come back to this laptop\nCtrl + Alt + M   = switch click mode\nCtrl + Alt + C   = put the B cursor in the middle"
            };
            Controls.Add(l);
        }
        // not click-through and allowed to take focus, so scroll messages land here
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
        }
    }

    class ClientForm : Form
    {
        readonly TextBox txtHost = new TextBox(), txtPin = new TextBox();
        readonly Button btnConnect = new Button(), btnFind = new Button(), btnCapture = new Button(), btnLive = new Button();
        readonly Label lblStatus = new Label(), lblLag = new Label();
        ViewerForm viewer;
        volatile IntPtr viewerHandle;
        readonly CaptureCurtain curtain = new CaptureCurtain();
        readonly System.Windows.Forms.Timer pingTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer retryTimer = new System.Windows.Forms.Timer();
        bool retrying, connecting;

        TcpClient tcp;
        Stream stream;
        BlockingCollection<byte[]> outQ;
        volatile bool connected;
        volatile bool capturing;
        POINT center;

        // hooks
        static HookProc mouseProc, keyProc;
        IntPtr mouseHook, keyHook;
        readonly bool[] localKeys = new bool[256], sentKeys = new bool[256], swallowUp = new bool[256];
        readonly bool[] localBtn = new bool[5], sentBtn = new bool[5];
        bool lctrl, rctrl, lalt, ralt;

        public ClientForm()
        {
            Text = "DuoSeat - Player B";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(420, 250);

            Label l1 = new Label { Text = "Player A's address:", Bounds = new Rectangle(14, 12, 130, 24), TextAlign = ContentAlignment.MiddleLeft };
            txtHost.Bounds = new Rectangle(146, 12, 170, 24);
            btnFind.Text = "Find"; btnFind.Bounds = new Rectangle(322, 11, 84, 26);
            btnFind.Click += delegate { Find(); };
            Label l2 = new Label { Text = "PIN (shown on A):", Bounds = new Rectangle(14, 46, 130, 24), TextAlign = ContentAlignment.MiddleLeft };
            txtPin.Bounds = new Rectangle(146, 46, 80, 24);
            btnConnect.Text = "Connect"; btnConnect.Bounds = new Rectangle(14, 84, 110, 32);
            btnConnect.Click += delegate
            {
                if (connected || retrying)
                {
                    retrying = false; retryTimer.Stop();
                    if (viewer != null) viewer.Close();
                    Disconnect("Disconnected.");
                }
                else Connect();
            };
            btnCapture.Text = "Take control"; btnCapture.Bounds = new Rectangle(130, 84, 134, 32);
            btnCapture.Enabled = false;
            btnCapture.Click += delegate { SetCapture(true); };
            btnLive.Text = "Live view"; btnLive.Bounds = new Rectangle(270, 84, 136, 32);
            btnLive.Enabled = false;
            btnLive.Click += delegate { OpenViewer(); };
            lblStatus.Bounds = new Rectangle(14, 124, 392, 40);
            lblStatus.Text = "Both laptops must be on the same Wi-Fi / network.";
            lblLag.Bounds = new Rectangle(306, 60, 100, 18); lblLag.TextAlign = ContentAlignment.MiddleRight; lblLag.ForeColor = Color.DimGray;
            Label help = new Label
            {
                Text = "Take control: look at A's TV, your mouse + keys drive the orange cursor.\nLive view: see A's screen (or the virtual one) right here.\nCtrl+Alt+X take over / come back   Ctrl+Alt+M click mode\nCtrl+Alt+C cursor to the middle",
                Bounds = new Rectangle(14, 166, 392, 70), ForeColor = Color.DimGray
            };
            Controls.AddRange(new Control[] { l1, txtHost, btnFind, l2, txtPin, lblLag, btnConnect, btnCapture, btnLive, lblStatus, help });
            Aero.Dress(this, "Player B", "Your mouse and keyboard, on Player A's screen.", 0);

            retryTimer.Interval = 3000;
            retryTimer.Tick += delegate { if (retrying && !connected && !connecting) Connect(); };

            try { string[] last = File.ReadAllLines(SettingsPath()); if (last.Length > 0) txtHost.Text = last[0]; } catch { }

            pingTimer.Interval = 1000;
            pingTimer.Tick += delegate { Send(Proto.Ping, Environment.TickCount | 1, 0, 0); };

            curtain.Wheel = delegate (int delta, bool horizontal) { if (capturing) Send(Proto.Wheel, delta, horizontal ? 1 : 0, 0); };
            mouseProc = MouseHook;
            keyProc = KeyHook;
        }

        static string SettingsPath()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DuoSeat");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "client.txt");
        }

        bool autoLive;

        // DuoSeat.exe --client --connect 192.168.0.10 --pin 1234 [--live]   (a desktop shortcut can do this)
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            string[] a = Environment.GetCommandLineArgs();
            string host = null, pin = null;
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i] == "--connect" && i + 1 < a.Length) host = a[i + 1];
                if (a[i] == "--pin" && i + 1 < a.Length) pin = a[i + 1];
                if (a[i] == "--live") autoLive = true;
            }
            if (host == null) return;
            txtHost.Text = host;
            if (pin != null) txtPin.Text = pin;
            Connect();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Disconnect(null);
            base.OnFormClosing(e);
        }

        void Status(string s) { if (InvokeRequired) BeginInvoke((Action)delegate { lblStatus.Text = s; }); else lblStatus.Text = s; }

        // ------------------------------------------------------------------ discovery
        void Find()
        {
            btnFind.Enabled = false;
            Status("Looking for Player A on the network...");
            Thread t = new Thread(delegate ()
            {
                List<string> found = new List<string>();
                try
                {
                    using (UdpClient u = new UdpClient())
                    {
                        u.EnableBroadcast = true;
                        byte[] msg = Encoding.ASCII.GetBytes(Proto.DiscoverMsg);
                        foreach (IPAddress bc in Broadcasts())
                            try { u.Send(msg, msg.Length, new IPEndPoint(bc, Proto.UdpPort)); } catch { }
                        u.Client.ReceiveTimeout = 400;
                        DateTime end = DateTime.Now.AddSeconds(1.6);
                        while (DateTime.Now < end)
                        {
                            try
                            {
                                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                                string r = Encoding.UTF8.GetString(u.Receive(ref from));
                                if (r.StartsWith("DUOSEAT!")) { string ip = from.Address.ToString(); if (!found.Contains(ip)) found.Add(ip); }
                            }
                            catch (SocketException) { }
                        }
                    }
                }
                catch { }
                BeginInvoke((Action)delegate
                {
                    btnFind.Enabled = true;
                    if (found.Count == 0) Status("Player A not found. Is the host running? Check Windows Firewall on A, or type A's address.");
                    else { txtHost.Text = found[0]; Status("Found Player A at " + string.Join(", ", found.ToArray()) + ". Enter the PIN and connect."); txtPin.Focus(); }
                });
            }) { IsBackground = true };
            t.Start();
        }

        static List<IPAddress> Broadcasts()
        {
            List<IPAddress> r = new List<IPAddress> { IPAddress.Broadcast };
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    byte[] ip = ua.Address.GetAddressBytes(), mask = ua.IPv4Mask.GetAddressBytes();
                    byte[] b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    r.Add(new IPAddress(b));
                }
            }
            return r;
        }

        // ------------------------------------------------------------------ connection
        void Connect()
        {
            string host = txtHost.Text.Trim(), pin = txtPin.Text.Trim();
            if (host.Length == 0) { Status("Type Player A's address or click Find."); return; }
            btnConnect.Enabled = retrying;
            connecting = true;
            if (!retrying) Status("Connecting to " + host + "...");
            Thread t = new Thread(delegate ()
            {
                try
                {
                    TcpClient c = new TcpClient();
                    IAsyncResult ar = c.BeginConnect(host, Proto.TcpPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) { c.Close(); throw new Exception("no answer (is DuoSeat open as Player A, and was it allowed through the Windows firewall?)"); }
                    c.EndConnect(ar);
                    c.NoDelay = true;
                    NetworkStream raw = c.GetStream();
                    // encrypted from here on: the PIN never crosses the network (Secure.cs)
                    Spake.Party sp = new Spake.Party(false, "p", pin);
                    Proto.WriteLine(raw, Proto.Hello + " " + Spake.Hex(sp.Mine) + " " + Environment.MachineName);
                    c.ReceiveTimeout = 8000;
                    string first;
                    try { first = Proto.ReadLine(raw, 2048); }
                    catch (IOException) { c.Close(); throw new Exception("Player A hung up. If it's an older DuoSeat, get the new one on both laptops."); }
                    if (first == "UPDATE" || first == "BADPIN") { c.Close(); throw new Exception("Player A has an older DuoSeat. Get the new one on both laptops."); }
                    if (first == "LOCKED") { c.Close(); throw new Exception("too many wrong tries on Player A - wait a few minutes"); }
                    string[] yp = first.Split(' ');
                    if (yp.Length < 3 || yp[0] != "Y") { c.Close(); throw new Exception("unexpected reply"); }
                    if (!sp.Finish(Spake.Unhex(yp[1])) || !Spake.Same(Spake.Unhex(yp[2]), sp.ConfirmA))
                    {
                        try { Proto.WriteLine(raw, "C 00"); } catch { }
                        c.Close(); throw new Exception("wrong PIN - check the PIN on Player A's screen");
                    }
                    Proto.WriteLine(raw, "C " + Spake.Hex(sp.ConfirmB));
                    Stream s = new SecureStream(raw, sp.MakeBox());
                    string reply = Proto.ReadLine(s, 200);
                    if (reply == "BADPIN") { c.Close(); throw new Exception("wrong PIN - check the PIN on Player A's screen"); }
                    if (reply == "BUSY") { c.Close(); throw new Exception("another Player B is already connected"); }
                    if (!reply.StartsWith("OK")) { c.Close(); throw new Exception("unexpected reply"); }
                    c.ReceiveTimeout = 0;
                    BeginInvoke((Action)delegate { OnConnected(c, s, reply.Length > 3 ? reply.Substring(3) : host, host); });
                }
                catch (Exception ex)
                {
                    bool badPin = ex.Message.StartsWith("wrong PIN");
                    BeginInvoke((Action)delegate
                    {
                        connecting = false;
                        btnConnect.Enabled = true;
                        if (retrying && !badPin) { Status("Lost Player A (sleep? Wi-Fi?). Reconnecting every 3 s..."); return; }
                        if (retrying) { retrying = false; retryTimer.Stop(); ResetButtons(); }
                        Status("Could not connect: " + ex.Message);
                    });
                }
            }) { IsBackground = true };
            t.Start();
        }

        void OnConnected(TcpClient c, Stream s, string name, string host)
        {
            tcp = c; stream = s; connected = true; connecting = false;
            retrying = false; retryTimer.Stop();
            try { File.WriteAllLines(SettingsPath(), new[] { host }); } catch { }
            outQ = new BlockingCollection<byte[]>();
            BlockingCollection<byte[]> q = outQ;
            Thread sender = new Thread(delegate ()
            {
                try { foreach (byte[] p in q.GetConsumingEnumerable()) s.Write(p, 0, p.Length); }
                catch { }
                if (connected) BeginInvoke((Action)delegate { LostConnection(); });
            }) { IsBackground = true };
            sender.Start();
            Thread reader = new Thread(delegate ()
            {
                // host -> app: [kind][int32 length][payload]
                try
                {
                    byte[] head = new byte[5];
                    while (Proto.ReadExact(s, head))
                    {
                        int len = BitConverter.ToInt32(head, 1);
                        if (len < 0 || len > 32 * 1024 * 1024) break;
                        byte[] data = new byte[len];
                        if (len > 0 && !Proto.ReadExact(s, data)) break;
                        if (head[0] == Proto.MsgFrame)
                        {
                            ViewerForm v = viewer;
                            if (v != null) v.PushFrame(data); else Send(Proto.Stream, 0, 0, 0);
                        }
                        else if (head[0] == Proto.MsgHint && len >= 1 && data[0] == 1)
                        {
                            BeginInvoke((Action)delegate
                            {
                                if (viewer != null || !connected) return;
                                OpenViewer();
                                Status("You share Player A's screen, so here it is live. Move over it and click or type: you control it, A keeps their own cursor.");
                            });
                        }
                        else if (head[0] == Proto.MsgPong && len >= 4)
                        {
                            int ms = Environment.TickCount - BitConverter.ToInt32(data, 0);
                            if (ms >= 0 && ms < 60000) BeginInvoke((Action)delegate { lblLag.Text = "lag " + ms + " ms"; });
                        }
                    }
                }
                catch { }
                if (connected) BeginInvoke((Action)delegate { LostConnection(); });
            }) { IsBackground = true };
            reader.Start();

            IntPtr mod = W.GetModuleHandle(null);
            mouseHook = W.SetWindowsHookEx(W.WH_MOUSE_LL, mouseProc, mod, 0);
            keyHook = W.SetWindowsHookEx(W.WH_KEYBOARD_LL, keyProc, mod, 0);
            pingTimer.Start();

            btnConnect.Text = "Disconnect"; btnConnect.Enabled = true;
            btnCapture.Enabled = true; btnLive.Enabled = true;
            if (viewer != null) Send(Proto.Stream, 1, 0, 0);   // reconnected with the viewer open
            else if (autoLive) { autoLive = false; OpenViewer(); }
            txtHost.Enabled = txtPin.Enabled = btnFind.Enabled = false;
            Status("Connected to " + name + ". Press Ctrl+Alt+X (or the button) to take control.");
        }

        void Disconnect(string why)
        {
            if (capturing) SetCapture(false);
            bool was = connected;
            connected = false;
            pingTimer.Stop();
            if (mouseHook != IntPtr.Zero) { W.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            if (keyHook != IntPtr.Zero) { W.UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
            if (outQ != null) { try { outQ.CompleteAdding(); } catch { } }
            Thread.Sleep(was ? 50 : 0);
            try { if (tcp != null) tcp.Close(); } catch { }
            tcp = null; stream = null;
            Array.Clear(sentKeys, 0, 256); Array.Clear(sentBtn, 0, 5);
            if (IsDisposed) return;
            ResetButtons();
            if (why != null) Status(why);
        }

        void ResetButtons()
        {
            btnConnect.Text = "Connect"; btnConnect.Enabled = true;
            btnCapture.Enabled = false; btnLive.Enabled = false;
            lblLag.Text = "";
            txtHost.Enabled = txtPin.Enabled = btnFind.Enabled = true;
        }

        // A went to sleep, Wi-Fi blinked, DuoSeat restarted... keep trying with the same address + PIN.
        void LostConnection()
        {
            Disconnect(null);
            retrying = true;
            btnConnect.Text = "Stop";
            txtHost.Enabled = txtPin.Enabled = btnFind.Enabled = false;
            Status("Lost Player A (sleep? Wi-Fi?). Reconnecting every 3 s...");
            retryTimer.Start();
        }

        // ------------------------------------------------------------------ live view window
        void OpenViewer()
        {
            if (viewer != null) { viewer.Activate(); return; }
            viewer = new ViewerForm(this);
            viewer.FormClosed += delegate
            {
                viewer = null; viewerHandle = IntPtr.Zero;
                Send(Proto.Stream, 0, 0, 0);
            };
            viewer.Show();
            viewerHandle = viewer.Handle;
            Send(Proto.Stream, 1, 0, 0);
        }

        public bool Capturing { get { return capturing; } }

        bool ViewerActive()
        {
            IntPtr v = viewerHandle;
            return v != IntPtr.Zero && W.GetForegroundWindow() == v;
        }

        internal void Send(byte type, int a, int b, int c)
        {
            BlockingCollection<byte[]> q = outQ;
            if (!connected || q == null || q.IsAddingCompleted) return;
            try { q.Add(Proto.Pack(type, a, b, c)); } catch { }
        }

        // ------------------------------------------------------------------ capture on/off
        void SetCapture(bool on)
        {
            if (on && !connected) return;
            if (on == capturing) return;
            if (on)
            {
                Rectangle sc = Screen.PrimaryScreen.Bounds;
                center = new POINT(sc.Left + sc.Width / 2, sc.Top + sc.Height / 2);
                W.SetCursorPos(center.X, center.Y);
                curtain.Bounds = sc;
                curtain.Show();
                curtain.Activate();   // focused too, for PCs with "scroll inactive windows" turned off
                capturing = true;
                Status("Controlling Player A's screen. Ctrl+Alt+X to come back.");
            }
            else
            {
                capturing = false;
                Send(Proto.ReleaseAll, 0, 0, 0);
                Array.Clear(sentBtn, 0, 5);
                curtain.Hide();
                if (connected) Status("Connected. Press Ctrl+Alt+X to take control again.");
            }
        }

        // ------------------------------------------------------------------ hooks
        IntPtr MouseHook(int n, IntPtr w, IntPtr l)
        {
            if (n < 0) return W.CallNextHookEx(mouseHook, n, w, l);
            MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
            int msg = (int)w;
            int btn = -1; bool down = false;
            switch (msg)
            {
                case W.WM_LBUTTONDOWN: btn = 0; down = true; break;
                case W.WM_LBUTTONUP: btn = 0; break;
                case W.WM_RBUTTONDOWN: btn = 1; down = true; break;
                case W.WM_RBUTTONUP: btn = 1; break;
                case W.WM_MBUTTONDOWN: btn = 2; down = true; break;
                case W.WM_MBUTTONUP: btn = 2; break;
                case W.WM_XBUTTONDOWN: btn = (m.mouseData >> 16) == 1 ? 3 : 4; down = true; break;
                case W.WM_XBUTTONUP: btn = (m.mouseData >> 16) == 1 ? 3 : 4; break;
            }

            if (!capturing)
            {
                if (btn >= 0) localBtn[btn] = down;
                return W.CallNextHookEx(mouseHook, n, w, l);
            }

            if (btn >= 0)
            {
                if (!down && localBtn[btn]) { localBtn[btn] = false; return W.CallNextHookEx(mouseHook, n, w, l); }
                if (!down && !sentBtn[btn]) return new IntPtr(1);
                sentBtn[btn] = down;
                Send(Proto.Button, btn, down ? 1 : 0, 0);
                return new IntPtr(1);
            }
            switch (msg)
            {
                case W.WM_MOUSEMOVE:
                    {
                        int dx = m.pt.X - center.X, dy = m.pt.Y - center.Y;
                        if (dx != 0 || dy != 0) Send(Proto.Move, dx, dy, 0);
                        break;
                    }
                case W.WM_MOUSEWHEEL: Send(Proto.Wheel, (short)(m.mouseData >> 16), 0, 0); break;
                case W.WM_MOUSEHWHEEL: Send(Proto.Wheel, (short)(m.mouseData >> 16), 1, 0); break;
            }
            return new IntPtr(1); // swallow: this laptop's own cursor stays still
        }

        IntPtr KeyHook(int n, IntPtr w, IntPtr l)
        {
            if (n < 0) return W.CallNextHookEx(keyHook, n, w, l);
            KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(KBDLLHOOKSTRUCT));
            int vk = (int)(k.vkCode & 0xFF);
            bool up = (k.flags & W.LLKHF_UP) != 0;
            bool ext = (k.flags & W.LLKHF_EXTENDED) != 0;

            if (vk == W.VK_LCONTROL) lctrl = !up;
            if (vk == W.VK_RCONTROL) rctrl = !up;
            if (vk == W.VK_LMENU) lalt = !up;
            if (vk == W.VK_RMENU) ralt = !up;

            // hotkeys (handled on this laptop, never sent)
            if (!up && (lctrl || rctrl) && (lalt || ralt) && (vk == 'X' || vk == 'M' || vk == 'C'))
            {
                swallowUp[vk] = true;
                if (vk == 'X') { bool on = !capturing; BeginInvoke((Action)delegate { SetCapture(on); }); }
                else if (capturing && vk == 'M') Send(Proto.Mode, 0, 0, 0);
                else if (capturing && vk == 'C') Send(Proto.Center, 0, 0, 0);
                return new IntPtr(1);
            }
            if (up && swallowUp[vk]) { swallowUp[vk] = false; return new IntPtr(1); }

            // keys go to A's PC while taking control, or while the live view window is in front
            if (capturing || ViewerActive())
            {
                if (up && localKeys[vk]) { localKeys[vk] = false; return W.CallNextHookEx(keyHook, n, w, l); }
                if (up && !sentKeys[vk]) return new IntPtr(1);
                sentKeys[vk] = !up;
                Send(Proto.Key, vk, (int)k.scanCode, (ext ? 1 : 0) | (up ? 0 : 2));
                return new IntPtr(1);
            }

            if (up && sentKeys[vk]) { sentKeys[vk] = false; Send(Proto.Key, vk, (int)k.scanCode, ext ? 1 : 0); }
            localKeys[vk] = !up;
            return W.CallNextHookEx(keyHook, n, w, l);
        }
    }
}
