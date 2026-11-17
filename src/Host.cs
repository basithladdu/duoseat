// DuoSeat host (Player A's laptop).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DuoSeat
{
    static class HostProgram
    {
        public static void Run()
        {
            bool created;
            using (Mutex m = new Mutex(true, "DuoSeatHostSingleInstance", out created))
            {
                // restarting as admin: wait for the old window to close
                if (!created && Array.IndexOf(Environment.GetCommandLineArgs(), "--elevated") > 0)
                {
                    try { created = m.WaitOne(8000); } catch (AbandonedMutexException) { created = true; }
                }
                if (!created) { MessageBox.Show("DuoSeat (Player A) is already open.", "DuoSeat"); return; }
                Application.Run(new HostForm());
            }
        }
    }

    // Click-through, never-activating, always-on-top window.
    class OverlayForm : Form
    {
        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            TopMost = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; // LAYERED|TRANSPARENT|TOOLWINDOW|NOACTIVATE|TOPMOST
                return cp;
            }
        }
    }

    class CursorOverlay : OverlayForm
    {
        public bool Borrow;
        public CursorOverlay() { Size = new Size(46, 50); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            float s = DeviceDpi / 96f;
            Point[] arrow = {
                P(1,1,s), P(1,24,s), P(7,18,s), P(11,28,s), P(15,26,s), P(11,17,s), P(19,17,s)
            };
            Color fill = Borrow ? Color.FromArgb(230, 40, 40) : Color.FromArgb(255, 140, 0);
            using (SolidBrush b = new SolidBrush(fill)) g.FillPolygon(b, arrow);
            using (Pen p = new Pen(Color.White, Math.Max(1.5f, 1.5f * s))) g.DrawPolygon(p, arrow);
            using (Font f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (SolidBrush bg = new SolidBrush(fill))
            {
                Rectangle tag = new Rectangle((int)(17 * s), (int)(18 * s), (int)(16 * s), (int)(15 * s));
                g.FillRectangle(bg, tag);
                TextRenderer.DrawText(g, "B", f, tag, Color.White, fill, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        static Point P(int x, int y, float s) { return new Point((int)(x * s), (int)(y * s)); }
        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Size = new Size((int)(46 * DeviceDpi / 96f), (int)(50 * DeviceDpi / 96f)); Invalidate(); }
    }

    class FrameOverlay : OverlayForm
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            using (Pen p = new Pen(Color.FromArgb(255, 140, 0), 4))
                e.Graphics.DrawRectangle(p, 2, 2, ClientSize.Width - 4, ClientSize.Height - 4);
        }
    }

    class MonitorItem
    {
        public string Name; public RECT Rect; public bool Virtual;
        public override string ToString() { return Name; }
    }

    class HostForm : Form
    {
        readonly Label lblAddr = new Label(), lblPin = new Label(), lblStatus = new Label();
        readonly ComboBox cboScreen = new ComboBox(), cboMode = new ComboBox();
        readonly CheckBox chkFrame = new CheckBox(), chkClip = new CheckBox();
        ClipSwapper clipSwap;
        readonly Button btnVirtual = new Button(), btnNet = new Button();
        readonly LinkLabel lnkNet = new LinkLabel();
        Tunnel tunnel;
        bool busyExtra;
        readonly CursorOverlay cursor = new CursorOverlay();
        readonly FrameOverlay frame = new FrameOverlay();
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        string pin;
        bool fixedPinUsed;
        volatile Injector active;
        volatile TcpClient activeClient;
        RECT area;
        TcpListener listener;

        public HostForm()
        {
            Text = "DuoSeat - Player A";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(440, 372);

            Label l1 = new Label { Text = "Player B connects with the DuoSeat app, or any phone/browser:", Bounds = new Rectangle(14, 10, 412, 20) };
            lblAddr.Bounds = new Rectangle(14, 30, 412, 40);
            lblAddr.Font = new Font("Consolas", 11f, FontStyle.Bold);
            lblAddr.ForeColor = Aero.Ink;
            Label l2 = new Label { Text = "PIN:", Bounds = new Rectangle(14, 76, 40, 30), TextAlign = ContentAlignment.MiddleLeft };
            lblPin.Bounds = new Rectangle(54, 72, 120, 36);
            lblPin.Font = new Font("Consolas", 20f, FontStyle.Bold);
            lblPin.ForeColor = Aero.Ink;
            Button btnPin = new Button { Text = "New PIN", Bounds = new Rectangle(180, 77, 90, 26) };
            btnPin.Click += delegate { NewPin(); };

            Label l3 = new Label { Text = "Player B's screen:", Bounds = new Rectangle(14, 120, 130, 24), TextAlign = ContentAlignment.MiddleLeft };
            cboScreen.Bounds = new Rectangle(150, 120, 276, 24);
            cboScreen.DropDownStyle = ComboBoxStyle.DropDownList;
            cboScreen.SelectedIndexChanged += delegate { ApplyArea(); };

            Label l4 = new Label { Text = "Click mode:", Bounds = new Rectangle(14, 154, 130, 24), TextAlign = ContentAlignment.MiddleLeft };
            cboMode.Bounds = new Rectangle(150, 154, 276, 24);
            cboMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cboMode.Items.Add("Shared - no clashing (recommended)");
            cboMode.Items.Add("Borrow - works in every app, A's cursor blinks");
            cboMode.SelectedIndex = 0;
            cboMode.SelectedIndexChanged += delegate { Injector inj = active; if (inj != null) inj.BorrowMode = cboMode.SelectedIndex == 1; cursor.Borrow = cboMode.SelectedIndex == 1; cursor.Invalidate(); };

            chkFrame.Text = "Orange frame around B's window";
            chkFrame.Bounds = new Rectangle(14, 186, 210, 24);
            chkFrame.Checked = true;
            chkClip.Text = "Own clipboard for B (beta)";
            chkClip.Bounds = new Rectangle(228, 186, 200, 24);
            chkClip.CheckedChanged += delegate { Injector inj = active; if (inj != null) inj.ClipSwap = chkClip.Checked ? clipSwap : null; };
            clipSwap = new ClipSwapper(this);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--own-clipboard") > 0) chkClip.Checked = true;

            btnVirtual.Bounds = new Rectangle(14, 216, 136, 30);
            btnVirtual.Click += delegate { VirtualClicked(); };
            btnNet.Text = "Internet link"; btnNet.Bounds = new Rectangle(154, 216, 132, 30);
            btnNet.Click += delegate { NetClicked(); };
            Button btnAdmin = new Button { Text = IsAdmin() ? "Admin: on" : "Run as admin", Bounds = new Rectangle(290, 216, 136, 30), Enabled = !IsAdmin() };
            btnAdmin.Click += delegate { Elevate(); };
            lnkNet.Bounds = new Rectangle(14, 252, 412, 22);
            lnkNet.Font = new Font("Consolas", 9.5f, FontStyle.Bold);
            lnkNet.LinkClicked += delegate { if (tunnel != null && tunnel.Link != null) { try { Clipboard.SetText(tunnel.Link); lblStatus.Text = "Link copied. Send it to Player B with the PIN."; } catch { } } };

            lblStatus.Bounds = new Rectangle(14, 278, 412, 48);
            lblStatus.Text = "Waiting for Player B...";
            Label tip = new Label
            {
                Text = "Tip: fewest surprises when A and B use different apps (say A on Chrome, B on Edge).",
                Bounds = new Rectangle(14, 328, 412, 40), ForeColor = Color.DimGray
            };

            Controls.AddRange(new Control[] { l1, lblAddr, l2, lblPin, btnPin, l3, cboScreen, l4, cboMode, chkFrame, chkClip, btnVirtual, btnNet, btnAdmin, lnkNet, lblStatus, tip });
            Aero.Dress(this, "Player A", "Your cursor stays yours. Player B gets the orange one.", 0);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            LoadMonitors();
            NewPin();
            ShowAddresses();

            uiTimer.Interval = 120;
            uiTimer.Tick += delegate { UpdateFrame(); };
            uiTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            cursor.Show(); cursor.Hide();
            frame.Show(); frame.Hide();
            StartServer();
        }

        bool hasVirtual;

        // ------------------------------------------------------------------ extras
        void VirtualClicked()
        {
            if (busyExtra) return;
            bool removing = hasVirtual;
            string ask = removing
                ? "Remove Player B's virtual screen?\n\nWindows will ask for permission."
                : "Give Player B a screen of their own, no monitor needed?\n\n" +
                  "DuoSeat adds a virtual screen using the free, open-source Virtual Display Driver (MIT, github.com/VirtualDrivers). " +
                  "It downloads the official release (70 MB, checked by SHA-256) and Windows asks for permission once.\n\n" +
                  "Player B then works on it through Live view.";
            if (MessageBox.Show(this, ask, "DuoSeat", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            busyExtra = true; btnVirtual.Enabled = false;
            Thread t = new Thread(delegate ()
            {
                string msg;
                try
                {
                    bool ok = removing ? VirtualScreen.Remove(Status) : VirtualScreen.Install(Status);
                    msg = !ok ? "Cancelled at the Windows permission prompt."
                        : removing ? "Virtual screen removed." : "Adding the virtual screen... (it appears in a few seconds)";
                }
                catch (Exception ex) { msg = "Virtual screen: " + ex.Message; }
                BeginInvoke((Action)delegate { busyExtra = false; btnVirtual.Enabled = true; lblStatus.Text = msg; LoadMonitorsKeep(); });
            }) { IsBackground = true };
            t.Start();
        }

        void LoadMonitorsKeep() { OnDisplayChanged(null, EventArgs.Empty); }

        void NetClicked()
        {
            if (tunnel != null)
            {
                tunnel.Stop(); tunnel = null;
                btnNet.Text = "Internet link"; lnkNet.Text = "";
                NewPin(4);
                lblStatus.Text = "Internet link closed. Only this Wi-Fi can connect now.";
                return;
            }
            if (MessageBox.Show(this,
                "Let Player B connect from anywhere (another network, another city)?\n\n" +
                "DuoSeat opens a private https link using Cloudflare's free tunnel (their official, signed cloudflared tool). " +
                "Player B opens the link in any browser and types the PIN.\n\nThe PIN becomes 8 digits while the link is open. Close it with the same button.",
                "DuoSeat", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            NewPin(8);
            tunnel = new Tunnel();
            btnNet.Text = "Close link"; btnNet.Enabled = false;
            Tunnel me = tunnel;
            me.Start(Status,
                delegate (string link)
                {
                    BeginInvoke((Action)delegate
                    {
                        if (tunnel != me) return;
                        btnNet.Enabled = true;
                        lnkNet.Text = link + "   (click to copy)";
                        lblStatus.Text = "Internet link is live. Send the link + PIN to Player B.";
                    });
                },
                delegate (string err)
                {
                    BeginInvoke((Action)delegate
                    {
                        if (tunnel != me) return;
                        me.Stop(); tunnel = null; btnNet.Enabled = true; btnNet.Text = "Internet link"; lnkNet.Text = "";
                        NewPin(4);
                        lblStatus.Text = "Internet link failed: " + err;
                    });
                });
        }

        static bool IsAdmin()
        {
            try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // Admin windows (Task Manager, installers, admin terminals) only take input from admin apps.
        void Elevate()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--host --elevated")
                { Verb = "runas", UseShellExecute = true });
                Close();
            }
            catch (System.ComponentModel.Win32Exception) { lblStatus.Text = "Stayed normal (permission prompt was cancelled)."; }
        }

        // monitor or TV plugged in / unplugged / resolution changed
        void OnDisplayChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated) return;
            BeginInvoke((Action)delegate
            {
                string keep = cboScreen.SelectedItem == null ? null : cboScreen.SelectedItem.ToString();
                bool hadVirtual = hasVirtual;
                int before = cboScreen.Items.Count;
                LoadMonitors();
                if (hasVirtual && !hadVirtual)
                {
                    ApplyArea();   // LoadMonitors already picked the new virtual screen
                    lblStatus.Text = "Virtual screen ready. It's Player B's: they see it in Live view.";
                    return;
                }
                if (before <= 1 && cboScreen.Items.Count > 1)
                {
                    ApplyArea();   // TV / monitor just plugged in: that's B's screen now
                    lblStatus.Text = "New screen found. Player B moved onto it.";
                    return;
                }
                bool found = false;
                if (keep != null)
                    for (int i = 0; i < cboScreen.Items.Count; i++)
                        if (cboScreen.Items[i].ToString() == keep) { cboScreen.SelectedIndex = i; found = true; }
                ApplyArea();
                if (keep != null && !found)
                    lblStatus.Text = cboScreen.Items.Count == 1
                        ? "Extra screen unplugged. Player B now shares the laptop screen (best with the browser Live view)."
                        : "Screens changed. Player B is now on: " + cboScreen.SelectedItem;
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            if (tunnel != null) tunnel.Stop();
            try { if (listener != null) listener.Stop(); } catch { }
            try { TcpClient c = activeClient; if (c != null) c.Close(); } catch { }
            base.OnFormClosing(e);
        }

        void NewPin() { NewPin(tunnel != null ? 8 : 4); }

        void NewPin(int digits)
        {
            byte[] rnd = new byte[8];
            using (System.Security.Cryptography.RandomNumberGenerator rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(rnd);
            ulong v = BitConverter.ToUInt64(rnd, 0);
            ulong lo = digits >= 8 ? 10000000UL : 1000UL, span = digits >= 8 ? 90000000UL : 9000UL;
            pin = (lo + v % span).ToString();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i] == "--pin" && !fixedPinUsed) { pin = args[i + 1]; fixedPinUsed = true; }
            lblPin.Text = pin;
        }

        void ShowAddresses()
        {
            List<string> ips = new List<string>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                string d = (ni.Description + ni.Name).ToLowerInvariant();
                if (d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") || d.Contains("vethernet") || d.Contains("bluetooth")) continue;
                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) ips.Add(ua.Address.ToString());
            }
            lblAddr.Text = ips.Count == 0 ? "(no network - join the same Wi-Fi)"
                : "App:     " + ips[0] + "  (or Find)\nBrowser: http://" + ips[0] + ":" + Proto.TcpPort;
        }

        void LoadMonitors()
        {
            List<MonitorItem> mons = new List<MonitorItem>();
            int idx = 0;
            W.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr h, IntPtr dc, ref RECT r, IntPtr d)
            {
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFOEX));
                W.GetMonitorInfoEx(h, ref mi);
                idx++;
                bool primary = (mi.dwFlags & 1) != 0;
                bool virt = VirtualScreen.IsVirtual(mi.szDevice);
                mons.Add(new MonitorItem
                {
                    Name = "Screen " + idx + (virt ? " - virtual (B's own)" : primary ? " - laptop" : " - extra monitor") + " (" + mi.rcMonitor.Width + "x" + mi.rcMonitor.Height + ")",
                    Rect = mi.rcMonitor, Virtual = virt
                });
                return true;
            }, IntPtr.Zero);
            hasVirtual = mons.Exists(delegate (MonitorItem m) { return m.Virtual; });
            btnVirtual.Text = hasVirtual ? "Remove virtual screen" : "Virtual screen for B";
            RECT all = new RECT
            {
                Left = W.GetSystemMetrics(W.SM_XVIRTUALSCREEN),
                Top = W.GetSystemMetrics(W.SM_YVIRTUALSCREEN)
            };
            all.Right = all.Left + W.GetSystemMetrics(W.SM_CXVIRTUALSCREEN);
            all.Bottom = all.Top + W.GetSystemMetrics(W.SM_CYVIRTUALSCREEN);
            cboScreen.Items.Clear();
            int pick = 0;
            for (int i = 0; i < mons.Count; i++)
            {
                cboScreen.Items.Add(mons[i]);
                if (mons[i].Name.Contains("extra") && !mons[pick].Virtual) pick = i;
                if (mons[i].Virtual) pick = i;          // a virtual screen was made for B: use it
            }
            if (mons.Count > 1) cboScreen.Items.Add(new MonitorItem { Name = "All screens", Rect = all });
            cboScreen.SelectedIndex = pick;
        }

        void ApplyArea()
        {
            MonitorItem m = cboScreen.SelectedItem as MonitorItem;
            if (m == null) return;
            area = m.Rect;
            Injector inj = active;
            if (inj != null) inj.SetArea(area);
        }

        void UpdateFrame()
        {
            Injector inj = active;
            IntPtr top = inj == null ? IntPtr.Zero : inj.BTop;
            RECT r;
            if (inj == null || !chkFrame.Checked || top == IntPtr.Zero || !W.IsWindow(top) || !W.IsWindowVisible(top)
                || !W.GetWindowRect(top, out r) || r.Width < 40)
            {
                if (frame.Visible) frame.Hide();
                return;
            }
            if (!frame.Visible) frame.Show();
            W.SetWindowPos(frame.Handle, W.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, W.SWP_NOACTIVATE);
            W.SetWindowPos(cursor.Handle, W.HWND_TOPMOST, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOACTIVATE);
        }

        void Status(string s) { if (IsHandleCreated) BeginInvoke((Action)delegate { lblStatus.Text = s; }); }

        // ------------------------------------------------------------------ network
        void StartServer()
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, Proto.TcpPort);
                listener.Start();
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Could not open port " + Proto.TcpPort + ": " + ex.Message;
                return;
            }
            Thread t = new Thread(AcceptLoop) { IsBackground = true, Name = "accept" };
            t.Start();
            Thread u = new Thread(DiscoveryLoop) { IsBackground = true, Name = "discovery" };
            u.Start();
        }

        void DiscoveryLoop()
        {
            try
            {
                using (UdpClient udp = new UdpClient(Proto.UdpPort))
                {
                    while (true)
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = udp.Receive(ref from);
                        if (Encoding.ASCII.GetString(data) != Proto.DiscoverMsg) continue;
                        byte[] reply = Encoding.UTF8.GetBytes("DUOSEAT! " + Environment.MachineName);
                        udp.Send(reply, reply.Length, from);
                    }
                }
            }
            catch { }
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { return; }
                Thread t = new Thread(delegate () { Dispatch(c); }) { IsBackground = true, Name = "conn" };
                t.Start();
            }
        }

        // Same port for everything: the DuoSeat app says "DUOSEAT1 ...", a browser says "GET ...".
        void Dispatch(TcpClient c)
        {
            try
            {
                c.NoDelay = true;
                c.ReceiveTimeout = 15000;
                NetworkStream s = c.GetStream();
                string first = Proto.ReadLine(s, 2048);
                if (first.StartsWith("GET ")) { WebPlayer.Handle(this, c, s, first); return; }
                Session(c, s, first);
            }
            catch { }
            finally { try { c.Close(); } catch { } }
        }

        readonly object sessionLock = new object();
        public string Pin { get { return pin; } }
        public RECT Area { get { return area; } }
        public Injector Active { get { return active; } }

        public bool TryBegin(TcpClient c)
        {
            lock (sessionLock)
            {
                if (activeClient != null) return false;
                activeClient = c;
                return true;
            }
        }

        public void Log(string s) { Status(s); }

        // Called on the connection's own thread; that thread then feeds the injector.
        public Injector StartInjector(string name, string who)
        {
            // the injecting thread needs its own message queue (for AttachThreadInput)
            System.Windows.Forms.Message m; W.PeekMessage(out m, IntPtr.Zero, 0, 0, 0);
            Injector inj = new Injector(area);
            inj.CursorHwnd = cursor.Handle;
            inj.BorrowMode = (bool)Invoke((Func<bool>)delegate { return cboMode.SelectedIndex == 1; });
            inj.ClipSwap = (bool)Invoke((Func<bool>)delegate { return chkClip.Checked; }) ? clipSwap : null;
            inj.ModeChanged = delegate
            {
                BeginInvoke((Action)delegate
                {
                    Injector i2 = active;
                    if (i2 == null) return;
                    cboMode.SelectedIndex = i2.BorrowMode ? 1 : 0;
                });
            };
            active = inj;
            BeginInvoke((Action)delegate { cursor.Borrow = inj.BorrowMode; cursor.Show(); cursor.Invalidate(); });
            inj.CenterCursor();
            Status("Player B connected: " + name + " (" + who + ")");
            return inj;
        }

        public void EndSession(Injector inj)
        {
            if (inj != null) { try { inj.ReleaseAll(); } catch { } }
            lock (sessionLock) { active = null; activeClient = null; }
            if (IsHandleCreated) BeginInvoke((Action)delegate { cursor.Hide(); frame.Hide(); });
            Status("Player B disconnected. Waiting for Player B...");
        }

        // B (from the phone) picks which screen to see and use: -1 = next one
        public RECT SwitchScreen()
        {
            Invoke((Action)delegate
            {
                int n = cboScreen.Items.Count;
                if (n > 1) cboScreen.SelectedIndex = (cboScreen.SelectedIndex + 1) % n;
            });
            return area;
        }

        void Session(TcpClient c, NetworkStream s, string hello)
        {
            string who = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
            string[] parts = hello.Split(new[] { ' ' }, 3);
            if (parts.Length < 2 || parts[0] != Proto.Hello) return;
            if (Guard.Locked || parts[1] != pin)
            {
                if (!Guard.Locked) Guard.Failed();
                Proto.WriteLine(s, "BADPIN"); Status(Guard.Locked ? "Too many wrong PINs - locked for a minute." : "Wrong PIN from " + who);
                Thread.Sleep(300); return;
            }
            if (!TryBegin(c)) { Proto.WriteLine(s, "BUSY"); return; }
            Injector inj = null;
            object wlock = new object();
            bool alive = true, streaming = false;
            AutoResetEvent ack = new AutoResetEvent(false);
            Action<byte, byte[]> send = delegate (byte kind, byte[] data)
            {
                lock (wlock)
                {
                    byte[] head = new byte[5];
                    head[0] = kind;
                    BitConverter.GetBytes(data.Length).CopyTo(head, 1);
                    s.Write(head, 0, 5);
                    s.Write(data, 0, data.Length);
                }
            };
            try
            {
                string name = parts.Length > 2 ? parts[2] : who;
                Proto.WriteLine(s, "OK " + Environment.MachineName);
                inj = StartInjector(name, who);
                Injector injRef = inj;
                RECT lastArea = new RECT();

                // live picture for the app's viewer window: one frame per Ack, so it never backs up
                Thread streamer = new Thread(delegate ()
                {
                    DateTime last = DateTime.MinValue;
                    try
                    {
                        while (alive)
                        {
                            ack.WaitOne(1000);
                            if (!alive) break;
                            if (!streaming) continue;
                            RECT ar = injRef.Area;
                            if (ar.Width != lastArea.Width || ar.Height != lastArea.Height)
                            {
                                byte[] wh = new byte[8];
                                BitConverter.GetBytes(ar.Width).CopyTo(wh, 0); BitConverter.GetBytes(ar.Height).CopyTo(wh, 4);
                                send(Proto.MsgArea, wh); lastArea = ar;
                            }
                            double wait = 30 - (DateTime.UtcNow - last).TotalMilliseconds;   // up to ~30 fps on a LAN
                            if (wait > 0) Thread.Sleep((int)wait);
                            last = DateTime.UtcNow;
                            byte[] jpg = WebPlayer.Frame(injRef, 1920, 70L);
                            if (jpg != null) send(Proto.MsgFrame, jpg); else ack.Set();
                        }
                    }
                    catch { alive = false; }
                }) { IsBackground = true, Name = "app-stream" };
                streamer.Start();

                byte[] buf = new byte[Proto.PacketSize];
                while (Proto.ReadExact(s, buf))
                {
                    int a = BitConverter.ToInt32(buf, 1), b = BitConverter.ToInt32(buf, 5), cc = BitConverter.ToInt32(buf, 9);
                    switch (buf[0])
                    {
                        case Proto.Stream: streaming = a == 1; lastArea = new RECT(); ack.Set(); break;
                        case Proto.Ack: ack.Set(); break;
                        case Proto.Ping: if (a != 0) send(Proto.MsgPong, BitConverter.GetBytes(a)); break;
                        default: inj.Handle(buf[0], a, b, cc); break;
                    }
                }
            }
            catch (Exception) { }
            finally { alive = false; ack.Set(); EndSession(inj); }
        }
    }
}
