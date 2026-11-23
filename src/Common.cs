// DuoSeat - shared protocol + Win32 interop.
// C# 5 (builds with the csc.exe that ships with Windows / .NET Framework 4.x).
using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace DuoSeat
{
    // Wire protocol: after handshake, fixed 13-byte packets:
    //   byte type, int32 a, int32 b, int32 c   (little endian)
    public static class Proto
    {
        public const int TcpPort = 47800;
        public const int UdpPort = 47801;
        public const string Hello = "DUOSEAT2";      // v2 = encrypted (Secure.cs). "DUOSEAT1" was plain text.
        public const string DiscoverMsg = "DUOSEAT?";

        public const byte Move = 1;     // a=dx b=dy
        public const byte Button = 2;   // a=button(0 L,1 R,2 M,3 X1,4 X2) b=down(1/0)
        public const byte Wheel = 3;    // a=delta b=horizontal(1/0)
        public const byte Key = 4;      // a=vk b=scan c=flags(bit0 extended, bit1 down)
        public const byte Mode = 5;     // toggle shared/borrow
        public const byte ReleaseAll = 6; // client stopped capturing
        public const byte Ping = 7;
        public const byte Center = 8;   // put B cursor in middle of its area
        public const byte MoveAbs = 9;  // a,b = position inside B's area, 0..10000 (touch screens)
        public const byte Char = 10;    // a = unicode char, c = mods (1 Ctrl, 2 Alt, 4 Shift) (phone keyboards)
        public const byte Stream = 11;  // a = 1 start / 0 stop the live picture
        public const byte Ack = 12;     // client has shown a frame, send the next one

        // host -> app messages: byte kind, int32 length, payload
        public const byte MsgFrame = 1; // JPEG
        public const byte MsgPong = 2;  // echoes the client's Ping value (for the lag readout)
        public const byte MsgArea = 3;  // int32 width, int32 height of B's screen
        public const byte MsgHint = 4;  // byte 1 = B's screen is the one Player A is sitting at (no TV): watch it in Live view

        public const int PacketSize = 13;

        public static byte[] Pack(byte type, int a, int b, int c)
        {
            byte[] p = new byte[PacketSize];
            p[0] = type;
            BitConverter.GetBytes(a).CopyTo(p, 1);
            BitConverter.GetBytes(b).CopyTo(p, 5);
            BitConverter.GetBytes(c).CopyTo(p, 9);
            return p;
        }

        public static void WriteLine(Stream s, string line)
        {
            byte[] b = Encoding.UTF8.GetBytes(line + "\n");
            s.Write(b, 0, b.Length);
        }

        public static string ReadLine(Stream s, int max)
        {
            StringBuilder sb = new StringBuilder();
            while (sb.Length < max)
            {
                int ch = s.ReadByte();
                if (ch < 0) throw new IOException("closed");
                if (ch == '\n') break;
                sb.Append((char)ch);
            }
            return sb.ToString().Trim();
        }

        public static bool ReadExact(Stream s, byte[] buf)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = s.Read(buf, got, buf.Length - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);

    public static class W
    {
        // messages
        public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
            WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_RBUTTONDBLCLK = 0x206,
            WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MBUTTONDBLCLK = 0x209,
            WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_XBUTTONDBLCLK = 0x20D,
            WM_MOUSEHWHEEL = 0x20E;
        public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104,
            WM_SYSKEYUP = 0x105, WM_SYSCHAR = 0x106;
        public const int WM_NCHITTEST = 0x84, WM_SYSCOMMAND = 0x112, WM_SETFOCUS = 0x7, WM_NULL = 0;
        public const int SC_CLOSE = 0xF060, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
        public const int HTCLIENT = 1, HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTLEFT = 10, HTRIGHT = 11,
            HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17,
            HTCLOSE = 20, HTTRANSPARENT = -1;
        public const int MK_LBUTTON = 1, MK_RBUTTON = 2, MK_SHIFT = 4, MK_CONTROL = 8, MK_MBUTTON = 0x10,
            MK_XBUTTON1 = 0x20, MK_XBUTTON2 = 0x40;

        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int LLMHF_INJECTED = 1, LLKHF_EXTENDED = 1, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;

        public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        public const uint MOUSEEVENTF_MOVE = 1, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4,
            MOUSEEVENTF_RIGHTDOWN = 8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20,
            MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_XDOWN = 0x80, MOUSEEVENTF_XUP = 0x100,
            MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000,
            MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;

        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10,
            SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200, SWP_ASYNCWINDOWPOS = 0x4000;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new IntPtr(-1);

        public const int GA_ROOT = 2;
        public const int GCL_STYLE = -26, CS_DBLCLKS = 8;
        public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78,
            SM_CYVIRTUALSCREEN = 79, SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;
        public const uint GUI_INMENUMODE = 4, GUI_POPUPMENUMODE = 0x10, GUI_INMOVESIZE = 2;
        public const uint SMTO_ABORTIFHUNG = 2;

        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14,
            VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4,
            VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_F4 = 0x73;

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("user32.dll")] public static extern uint WaitForInputIdle(IntPtr hProcess, uint ms);

        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gi);
        [DllImport("user32.dll")] public static extern bool GetKeyboardState(byte[] s);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool SetKeyboardState(byte[] s);
        [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint tid);
        [DllImport("user32.dll")] public static extern short VkKeyScanEx(char ch, IntPtr hkl);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll")] public static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out] StringBuilder buf, int cch, uint flags, IntPtr hkl);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr64(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")] public static extern uint GetClassLong32(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] public static extern bool GetMonitorInfoEx(IntPtr hMon, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out System.Windows.Forms.Message m, IntPtr h, uint a, uint b, uint remove);

        // DPI
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr h, ref POINT p);

        public static void MakeDpiAware()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public static IntPtr MakeLParam(int lo, int hi) { return new IntPtr((int)((ushort)lo | ((uint)(ushort)hi << 16))); }

        public static string ClassOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
        }

        public static int ClassStyle(IntPtr h)
        {
            if (IntPtr.Size == 8) return (int)GetClassLongPtr64(h, GCL_STYLE).ToInt64();
            return (int)GetClassLong32(h, GCL_STYLE);
        }

        public static uint ThreadOf(IntPtr h) { uint pid; return GetWindowThreadProcessId(h, out pid); }
        public static uint ProcessOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }

        public static int SendTimeout(IntPtr h, int msg, IntPtr w, IntPtr l)
        {
            IntPtr r;
            if (SendMessageTimeout(h, msg, w, l, SMTO_ABORTIFHUNG, 150, out r) == IntPtr.Zero) return 0;
            return (int)r.ToInt64();
        }
    }
}
