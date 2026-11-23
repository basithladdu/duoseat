// Scripted fake "Player B" for testing the host on one machine.
// usage: TestDriver.exe <host> <pin> <startX> <startY> <script.txt>
using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace DuoSeat
{
    static class TestDriver
    {
        [DllImport("user32.dll")] static extern short VkKeyScan(char c);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);

        static Stream s;
        static int x, y;

        static void P(byte t, int a, int b, int c) { byte[] p = Proto.Pack(t, a, b, c); s.Write(p, 0, p.Length); }
        static void Key(int vk, bool down) { P(Proto.Key, vk, (int)MapVirtualKey((uint)vk, 0), down ? 2 : 0); }

        static int Main(string[] args)
        {
            TcpClient c = new TcpClient(args[0], Proto.TcpPort);
            c.NoDelay = true;
            NetworkStream ns = c.GetStream();
            Spake.Party sp = new Spake.Party(false, "p", args[1]);
            Proto.WriteLine(ns, Proto.Hello + " " + Spake.Hex(sp.Mine) + " TestDriver");
            string[] yh = Proto.ReadLine(ns, 2048).Split(' ');
            if (yh.Length < 3 || !sp.Finish(Spake.Unhex(yh[1])) || !Spake.Same(Spake.Unhex(yh[2]), sp.ConfirmA)) { Proto.WriteLine(ns, "C 00"); Console.WriteLine("handshake: wrong PIN (" + yh[0] + ")"); return 1; }
            Proto.WriteLine(ns, "C " + Spake.Hex(sp.ConfirmB));
            s = new SecureStream(ns, sp.MakeBox());
            string reply = Proto.ReadLine(s, 100);
            Console.WriteLine("handshake: " + reply);
            if (!reply.StartsWith("OK")) return 1;
            W.MakeDpiAware();
            x = int.Parse(args[2]); y = int.Parse(args[3]);
            if (x < 0)
            {
                // the host starts B in the middle of B's screen: the extra monitor/TV if there is one
                System.Drawing.Rectangle r = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                foreach (System.Windows.Forms.Screen sc in System.Windows.Forms.Screen.AllScreens) if (!sc.Primary) { r = sc.Bounds; break; }
                x = r.Left + r.Width / 2; y = r.Top + r.Height / 2;
            }
            P(Proto.Center, 0, 0, 0);
            foreach (string raw in File.ReadAllLines(args[4]))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] a = line.Split(new[] { ' ' }, 2);
                string arg = a.Length > 1 ? a[1] : "";
                Console.WriteLine("> " + line);
                if (a[0] == "gotowin")
                {
                    // gotowin <title> <dx> <dy>   (offset from the window's top-left; negative = from right/bottom)
                    string[] q = arg.Split(' ');
                    IntPtr h = FindWindow(null, q[0]);
                    RECT r; W.GetWindowRect(h, out r);
                    int ox = int.Parse(q[1]), oy = int.Parse(q[2]);
                    arg = (ox >= 0 ? r.Left + ox : r.Right + ox) + " " + (oy >= 0 ? r.Top + oy : r.Bottom + oy);
                    Console.WriteLine("  window " + q[0] + " at " + r.Left + "," + r.Top + " -> " + arg);
                    a[0] = "goto";
                }
                switch (a[0])
                {
                    case "goto":
                        {
                            string[] xy = arg.Split(' ');
                            int tx = int.Parse(xy[0]), ty = int.Parse(xy[1]);
                            // move in small steps like a real mouse
                            while (x != tx || y != ty)
                            {
                                int dx = Math.Max(-25, Math.Min(25, tx - x)), dy = Math.Max(-25, Math.Min(25, ty - y));
                                P(Proto.Move, dx, dy, 0); x += dx; y += dy; Thread.Sleep(4);
                            }
                            break;
                        }
                    case "click": P(Proto.Button, arg == "" ? 0 : int.Parse(arg), 1, 0); Thread.Sleep(40); P(Proto.Button, arg == "" ? 0 : int.Parse(arg), 0, 0); break;
                    case "down": P(Proto.Button, int.Parse(arg), 1, 0); break;
                    case "up": P(Proto.Button, int.Parse(arg), 0, 0); break;
                    case "wheel": P(Proto.Wheel, int.Parse(arg), 0, 0); break;
                    case "mode": P(Proto.Mode, 0, 0, 0); break;
                    case "sleep": Thread.Sleep(int.Parse(arg)); break;
                    case "vk": { int vk = Convert.ToInt32(arg, 16); Key(vk, true); Thread.Sleep(15); Key(vk, false); break; }
                    case "ctrl":
                        {
                            int vk = Convert.ToInt32(arg, 16);
                            Key(0xA2, true); Thread.Sleep(15); Key(vk, true); Thread.Sleep(15); Key(vk, false); Key(0xA2, false);
                            break;
                        }
                    case "type":
                        foreach (char ch in arg)
                        {
                            short r = VkKeyScan(ch);
                            int vk = r & 0xFF; bool shift = (r & 0x100) != 0;
                            if (shift) Key(0xA0, true);
                            Key(vk, true); Thread.Sleep(8); Key(vk, false);
                            if (shift) Key(0xA0, false);
                            Thread.Sleep(8);
                        }
                        break;
                }
            }
            Thread.Sleep(300);
            P(Proto.ReleaseAll, 0, 0, 0);
            Thread.Sleep(200);
            c.Close();
            return 0;
        }
    }
}
