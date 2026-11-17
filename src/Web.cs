// Player B from any browser (phone, tablet, another laptop): no install.
// Serves one page and speaks WebSocket on the same port as the app (47800).
//   browser -> host : short text commands ("a 5000 5000", "b 0 1", "c 65 0", ...)
//   host -> browser : "ok <w> <h>" and JPEG frames of B's screen (binary), one per "f" (ack)
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DuoSeat
{
    static class WebPlayer
    {
        static byte[] page;

        static byte[] Page()
        {
            if (page != null) return page;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DuoSeat.index.html"))
            using (MemoryStream m = new MemoryStream())
            {
                if (s != null) s.CopyTo(m);
                page = m.ToArray();
            }
            return page;
        }

        public static void Handle(HostForm host, TcpClient c, NetworkStream s, string requestLine)
        {
            string key = null; bool upgrade = false;
            while (true)
            {
                string h = Proto.ReadLine(s, 8192);
                if (h.Length == 0) break;
                int colon = h.IndexOf(':');
                if (colon < 0) continue;
                string name = h.Substring(0, colon).Trim().ToLowerInvariant(), val = h.Substring(colon + 1).Trim();
                if (name == "sec-websocket-key") key = val;
                if (name == "upgrade" && val.ToLowerInvariant().Contains("websocket")) upgrade = true;
            }
            string path = requestLine.Split(' ')[1];

            if (upgrade && key != null && path.StartsWith("/ws")) { Socket(host, c, s, key); return; }

            if (path == "/" || path.StartsWith("/?") || path == "/index.html")
            {
                Respond(s, "200 OK", "text/html; charset=utf-8", Page());
                return;
            }
            if (path == "/logo.png")
            {
                Respond(s, "200 OK", "image/png", Logo());
                return;
            }
            Respond(s, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("nope"));
        }

        static byte[] logo;
        static byte[] Logo()
        {
            if (logo != null) return logo;
            try
            {
                using (Icon ic = new Icon(Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath), 48, 48))
                using (Bitmap b = ic.ToBitmap())
                using (MemoryStream m = new MemoryStream()) { b.Save(m, ImageFormat.Png); logo = m.ToArray(); }
            }
            catch { logo = new byte[0]; }
            return logo;
        }

        static void Respond(NetworkStream s, string status, string type, byte[] body)
        {
            string head = "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                          "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        // ------------------------------------------------------------------ websocket session
        class Conn
        {
            public NetworkStream S;
            public readonly object WriteLock = new object();
            public volatile bool Alive = true, Streaming;
            public readonly AutoResetEvent Ack = new AutoResetEvent(false);

            public void Send(byte opcode, byte[] data)
            {
                lock (WriteLock)
                {
                    int n = data.Length;
                    byte[] head;
                    if (n < 126) head = new byte[] { (byte)(0x80 | opcode), (byte)n };
                    else if (n < 65536) head = new byte[] { (byte)(0x80 | opcode), 126, (byte)(n >> 8), (byte)n };
                    else
                    {
                        head = new byte[10]; head[0] = (byte)(0x80 | opcode); head[1] = 127;
                        for (int i = 0; i < 8; i++) head[9 - i] = (byte)((long)n >> (8 * i));
                    }
                    S.Write(head, 0, head.Length);
                    S.Write(data, 0, n);
                }
            }
            public void Text(string t) { Send(1, Encoding.UTF8.GetBytes(t)); }
        }

        static bool ReadFrame(NetworkStream s, out int opcode, out byte[] payload)
        {
            opcode = 0; payload = null;
            byte[] h = new byte[2];
            if (!Proto.ReadExact(s, h)) return false;
            opcode = h[0] & 0x0F;
            bool masked = (h[1] & 0x80) != 0;
            long len = h[1] & 0x7F;
            if (len == 126) { byte[] e = new byte[2]; if (!Proto.ReadExact(s, e)) return false; len = (e[0] << 8) | e[1]; }
            else if (len == 127) { byte[] e = new byte[8]; if (!Proto.ReadExact(s, e)) return false; len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | e[i]; }
            if (len > 1 << 20) return false;
            byte[] mask = new byte[4];
            if (masked && !Proto.ReadExact(s, mask)) return false;
            payload = new byte[len];
            if (len > 0 && !Proto.ReadExact(s, payload)) return false;
            if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
            return true;
        }

        static void Socket(HostForm host, TcpClient c, NetworkStream s, string key)
        {
            string accept;
            using (SHA1 sha = SHA1.Create())
                accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] hs = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
            s.Write(hs, 0, hs.Length);
            c.ReceiveTimeout = 30000;

            Conn conn = new Conn { S = s };
            string who = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
            Injector inj = null;
            bool began = false;
            try
            {
                int op; byte[] data;
                // first message: "p <pin>"
                if (!ReadFrame(s, out op, out data) || op != 1) return;
                string hello = Encoding.UTF8.GetString(data);
                if (!hello.StartsWith("p ") || hello.Substring(2).Trim() != host.Pin)
                {
                    conn.Text("bad"); host.Log("Wrong PIN from browser " + who); Thread.Sleep(300); return;
                }
                if (!host.TryBegin(c)) { conn.Text("busy"); return; }
                began = true;
                inj = host.StartInjector("browser", who);
                RECT a = inj.Area;
                conn.Text("ok " + a.Width + " " + a.Height);

                Injector injRef = inj;
                Thread streamer = new Thread(delegate () { Stream(conn, injRef); }) { IsBackground = true, Name = "stream" };
                streamer.Start();

                while (ReadFrame(s, out op, out data))
                {
                    if (op == 8) break;                                 // close
                    if (op == 9) { conn.Send(10, data); continue; }     // ping -> pong
                    if (op != 1) continue;
                    try { Command(host, conn, inj, Encoding.UTF8.GetString(data)); } catch { }
                }
            }
            catch { }
            finally
            {
                conn.Alive = false; conn.Ack.Set();
                if (began) host.EndSession(inj);
            }
        }

        static void Command(HostForm host, Conn conn, Injector inj, string line)
        {
            string[] p = line.Split(' ');
            int a = p.Length > 1 ? ToInt(p[1]) : 0, b = p.Length > 2 ? ToInt(p[2]) : 0;
            switch (p[0])
            {
                case "a": inj.Handle(Proto.MoveAbs, a, b, 0); break;
                case "m": inj.Handle(Proto.Move, a, b, 0); break;
                case "b": inj.Handle(Proto.Button, a, b, 0); break;
                case "w": inj.Handle(Proto.Wheel, a, b, 0); break;
                case "c": inj.Handle(Proto.Char, a, 0, b); break;
                case "k": inj.Tap(a, b); break;                         // vk, mods
                case "x": inj.Handle(Proto.Mode, 0, 0, 0); conn.Text("mode " + (inj.BorrowMode ? 1 : 0)); break;
                case "z": inj.Handle(Proto.Center, 0, 0, 0); break;
                case "s": conn.Streaming = a == 1; conn.Ack.Set(); break;
                case "f": conn.Ack.Set(); break;
                case "n":
                    {
                        RECT r = host.SwitchScreen();
                        conn.Text("ok " + r.Width + " " + r.Height);
                        break;
                    }
            }
        }

        static int ToInt(string s) { int v; int.TryParse(s, out v); return v; }

        // ------------------------------------------------------------------ live view
        static void Stream(Conn conn, Injector inj)
        {
            ImageCodecInfo jpeg = null;
            foreach (ImageCodecInfo ci in ImageCodecInfo.GetImageEncoders()) if (ci.MimeType == "image/jpeg") jpeg = ci;
            EncoderParameters ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 55L);
            DateTime last = DateTime.MinValue;
            try
            {
                while (conn.Alive)
                {
                    conn.Ack.WaitOne(1000);
                    if (!conn.Alive) break;
                    if (!conn.Streaming) continue;
                    double wait = 70 - (DateTime.UtcNow - last).TotalMilliseconds;   // at most ~14 fps
                    if (wait > 0) Thread.Sleep((int)wait);
                    last = DateTime.UtcNow;
                    byte[] jpg = Capture(inj, jpeg, ep);
                    if (jpg != null) conn.Send(2, jpg);
                    else conn.Ack.Set();
                }
            }
            catch { conn.Alive = false; }
        }

        static byte[] Capture(Injector inj, ImageCodecInfo jpeg, EncoderParameters ep)
        {
            RECT a = inj.Area;
            if (a.Width <= 0 || a.Height <= 0) return null;
            double scale = Math.Min(1.0, 1600.0 / a.Width);
            int w = (int)(a.Width * scale), h = (int)(a.Height * scale);
            using (Bitmap full = new Bitmap(a.Width, a.Height, PixelFormat.Format24bppRgb))
            using (Bitmap small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                try
                {
                    using (Graphics g = Graphics.FromImage(full))
                        g.CopyFromScreen(a.Left, a.Top, 0, 0, new Size(a.Width, a.Height), CopyPixelOperation.SourceCopy);
                }
                catch { return null; } // locked screen / UAC prompt: nothing to show
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(full, 0, 0, w, h);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // both cursors, so A and B can see what the other one is pointing at
                    POINT pa; W.GetCursorPos(out pa);
                    if (pa.X >= a.Left && pa.X < a.Right && pa.Y >= a.Top && pa.Y < a.Bottom)
                        Arrow(g, (float)((pa.X - a.Left) * scale), (float)((pa.Y - a.Top) * scale), Color.FromArgb(0, 120, 215), "A");
                    Arrow(g, (float)((inj.BX - a.Left) * scale), (float)((inj.BY - a.Top) * scale),
                        inj.BorrowMode ? Color.FromArgb(230, 40, 40) : Color.FromArgb(255, 140, 0), "B");
                }
                using (MemoryStream m = new MemoryStream())
                {
                    small.Save(m, jpeg, ep);
                    return m.ToArray();
                }
            }
        }

        static void Arrow(Graphics g, float x, float y, Color c, string tag)
        {
            float k = 1.15f;
            PointF[] p = {
                new PointF(x, y), new PointF(x, y + 22 * k), new PointF(x + 6 * k, y + 17 * k), new PointF(x + 10 * k, y + 26 * k),
                new PointF(x + 14 * k, y + 24 * k), new PointF(x + 10 * k, y + 16 * k), new PointF(x + 17 * k, y + 16 * k)
            };
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, p);
            using (Pen pen = new Pen(Color.White, 1.6f)) g.DrawPolygon(pen, p);
            using (Font f = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillRectangle(b, x + 15 * k, y + 17 * k, 13, 13);
                g.DrawString(tag, f, Brushes.White, x + 15 * k + 1, y + 17 * k - 1);
            }
        }
    }
}
