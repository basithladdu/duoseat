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
                if (!created) { MessageBox.Show("DuoSeat host is already running.", "DuoSeat"); return; }
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
        public string Name; public RECT Rect;
        public override string ToString() { return Name; }
    }

    class HostForm : Form
    {
        readonly Label lblAddr = new Label(), lblPin = new Label(), lblStatus = new Label();
        readonly ComboBox cboScreen = new ComboBox(), cboMode = new ComboBox();
        readonly CheckBox chkFrame = new CheckBox();
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
            ClientSize = new Size(440, 300);

            Label l1 = new Label { Text = "On Player B's laptop: open DuoSeat, pick Player B, connect to", Bounds = new Rectangle(14, 10, 412, 20) };
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

            chkFrame.Text = "Orange frame around Player B's window";
            chkFrame.Bounds = new Rectangle(14, 186, 412, 24);
            chkFrame.Checked = true;

            lblStatus.Bounds = new Rectangle(14, 216, 412, 36);
            lblStatus.Text = "Waiting for Player B...";
            Label tip = new Label
            {
                Text = "Tip: fewest surprises when A and B use different apps (say A on Chrome, B on Edge).",
                Bounds = new Rectangle(14, 254, 412, 40), ForeColor = Color.DimGray
            };

            Controls.AddRange(new Control[] { l1, lblAddr, l2, lblPin, btnPin, l3, cboScreen, l4, cboMode, chkFrame, lblStatus, tip });
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

        // monitor or TV plugged in / unplugged / resolution changed
        void OnDisplayChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated) return;
            BeginInvoke((Action)delegate
            {
                string keep = cboScreen.SelectedItem == null ? null : cboScreen.SelectedItem.ToString();
                LoadMonitors();
                if (keep != null)
                    for (int i = 0; i < cboScreen.Items.Count; i++)
                        if (cboScreen.Items[i].ToString() == keep) cboScreen.SelectedIndex = i;
                ApplyArea();
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            try { if (listener != null) listener.Stop(); } catch { }
            try { TcpClient c = activeClient; if (c != null) c.Close(); } catch { }
            base.OnFormClosing(e);
        }

        void NewPin()
        {
            pin = new Random().Next(1000, 10000).ToString();
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
            lblAddr.Text = ips.Count == 0 ? "(no network - connect both laptops to the same Wi-Fi)" : string.Join("   ", ips.ToArray()) + "   (or click Find)";
        }

        void LoadMonitors()
        {
            List<MonitorItem> mons = new List<MonitorItem>();
            int idx = 0;
            W.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr h, IntPtr dc, ref RECT r, IntPtr d)
            {
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
                W.GetMonitorInfo(h, ref mi);
                idx++;
                bool primary = (mi.dwFlags & 1) != 0;
                mons.Add(new MonitorItem
                {
                    Name = "Screen " + idx + (primary ? " - laptop" : " - extra monitor") + " (" + mi.rcMonitor.Width + "x" + mi.rcMonitor.Height + ")",
                    Rect = mi.rcMonitor
                });
                return true;
            }, IntPtr.Zero);
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
                if (mons[i].Name.Contains("extra")) pick = i;
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
                if (activeClient != null)
                {
                    try { NetworkStream s0 = c.GetStream(); Proto.WriteLine(s0, "BUSY"); c.Close(); } catch { }
                    continue;
                }
                activeClient = c;
                Thread t = new Thread(delegate () { Session(c); }) { IsBackground = true, Name = "session" };
                t.Start();
            }
        }

        void Session(TcpClient c)
        {
            string who = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
            Injector inj = null;
            try
            {
                c.NoDelay = true;
                c.ReceiveTimeout = 15000;
                NetworkStream s = c.GetStream();
                string hello = Proto.ReadLine(s, 200);
                string[] parts = hello.Split(new[] { ' ' }, 3);
                if (parts.Length < 2 || parts[0] != Proto.Hello) { c.Close(); return; }
                if (parts[1] != pin) { Proto.WriteLine(s, "BADPIN"); Status("Wrong PIN from " + who); Thread.Sleep(300); c.Close(); return; }
                string name = parts.Length > 2 ? parts[2] : who;
                Proto.WriteLine(s, "OK " + Environment.MachineName);

                // the injecting thread needs its own message queue (for AttachThreadInput)
                System.Windows.Forms.Message m; W.PeekMessage(out m, IntPtr.Zero, 0, 0, 0);

                inj = new Injector(area);
                inj.CursorHwnd = cursor.Handle;
                inj.BorrowMode = (bool)Invoke((Func<bool>)delegate { return cboMode.SelectedIndex == 1; });
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

                byte[] buf = new byte[Proto.PacketSize];
                while (Proto.ReadExact(s, buf))
                {
                    int a = BitConverter.ToInt32(buf, 1), b = BitConverter.ToInt32(buf, 5), cc = BitConverter.ToInt32(buf, 9);
                    inj.Handle(buf[0], a, b, cc);
                }
            }
            catch (Exception) { }
            finally
            {
                if (inj != null) { try { inj.ReleaseAll(); } catch { } }
                active = null;
                activeClient = null;
                try { c.Close(); } catch { }
                if (IsHandleCreated) BeginInvoke((Action)delegate { cursor.Hide(); frame.Hide(); });
                Status("Player B disconnected. Waiting for Player B...");
            }
        }
    }
}
