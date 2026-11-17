// DuoSeat host - turns Player B's input into a second, independent cursor + keyboard.
//
// Windows only has ONE real cursor and ONE keyboard focus. So Player B's input is never
// sent through the real cursor in "Shared" mode: clicks, wheel and keys are delivered
// straight to the window under B's own (drawn) cursor with PostMessage, and window
// move/resize/min/max/close are done directly. Player A's cursor and focus are untouched.
//
// Some windows (taskbar, Start, UWP/WinUI apps, menus) ignore posted input. For those
// (or always, in "Borrow" mode) a click borrows the real cursor for a few milliseconds,
// clicks at B's spot, and gives the cursor and the active window back to Player A.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DuoSeat
{
    public class Injector
    {
        enum Drag { None, Post, Move, Resize, NcButton, BorrowPending, BorrowDrag }

        public volatile bool BorrowMode;
        public RECT Area;                 // where B's cursor may go (physical pixels)
        public IntPtr CursorHwnd;         // B's cursor overlay window
        public IntPtr BTop { get { return bTop; } }
        public int BX { get { return bx; } }
        public int BY { get { return by; } }
        public Action ModeChanged;

        int bx, by;
        IntPtr bTop, bFocus;
        uint attachedThread;
        readonly uint myThread = W.GetCurrentThreadId();
        readonly uint myPid = W.GetCurrentProcessId();

        // B keyboard state
        readonly bool[] keyDown = new bool[256];
        bool caps;

        // B mouse state
        Drag drag = Drag.None;
        int heldButton = -1;
        int buttonMask;
        IntPtr pressedHwnd;
        int dragHit;
        RECT dragRect;
        int dragX, dragY;
        uint lastDownTick; int lastDownX, lastDownY, lastDownBtn = -1; IntPtr lastDownHwnd;
        uint lastCapTick; int lastCapX, lastCapY;

        static readonly HashSet<string> BorrowClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "#32768", "Windows.UI.Core.CoreWindow",
            "ApplicationFrameWindow", "ApplicationFrameInputSinkWindow", "TaskListThumbnailWnd",
            "XamlExplorerHostIslandWindow", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
            "Windows.UI.Input.InputSite.WindowClass", "Microsoft.UI.Content.DesktopChildSiteBridge",
            "Windows.UI.Composition.DesktopWindowContentBridge", "Xaml_WindowedPopupClass",
            "DesktopWindowXamlSource", "ForegroundStaging", "MultitaskingViewFrame", "TaskSwitcherWnd"
        };

        public Injector(RECT area)
        {
            Area = area;
            CenterCursor();
        }

        // ------------------------------------------------------------------ dispatch
        public void Handle(byte type, int a, int b, int c)
        {
            try
            {
                switch (type)
                {
                    case Proto.Move: OnMove(a, b); break;
                    case Proto.Button: if (b != 0) OnDown(a); else OnUp(a); break;
                    case Proto.Wheel: OnWheel(a, b != 0); break;
                    case Proto.Key: OnKey(a, b, (c & 1) != 0, (c & 2) != 0); break;
                    case Proto.Mode: BorrowMode = !BorrowMode; if (ModeChanged != null) ModeChanged(); break;
                    case Proto.ReleaseAll: ReleaseAll(); break;
                    case Proto.Center: CenterCursor(); break;
                    case Proto.MoveAbs:
                        {
                            int x = Area.Left + (int)((long)Math.Max(0, Math.Min(10000, a)) * (Area.Width - 1) / 10000);
                            int y = Area.Top + (int)((long)Math.Max(0, Math.Min(10000, b)) * (Area.Height - 1) / 10000);
                            OnMove(x - bx, y - by);
                            break;
                        }
                    case Proto.Char: TypeChar(a, c); break;
                }
            }
            catch (Exception) { /* never let one bad event kill the session */ }
        }

        public void CenterCursor()
        {
            bx = (Area.Left + Area.Right) / 2;
            by = (Area.Top + Area.Bottom) / 2;
            MoveOverlay();
        }

        public void SetArea(RECT r)
        {
            Area = r;
            Clamp();
            MoveOverlay();
        }

        void Clamp()
        {
            if (bx < Area.Left) bx = Area.Left;
            if (bx > Area.Right - 1) bx = Area.Right - 1;
            if (by < Area.Top) by = Area.Top;
            if (by > Area.Bottom - 1) by = Area.Bottom - 1;
        }

        void MoveOverlay()
        {
            if (CursorHwnd == IntPtr.Zero) return;
            W.SetWindowPos(CursorHwnd, W.HWND_TOPMOST, bx - 1, by - 1, 0, 0,
                W.SWP_NOSIZE | W.SWP_NOACTIVATE | W.SWP_ASYNCWINDOWPOS | W.SWP_SHOWWINDOW);
        }

        // ------------------------------------------------------------------ mouse
        void OnMove(int dx, int dy)
        {
            bx += dx; by += dy;
            Clamp();
            MoveOverlay();

            switch (drag)
            {
                case Drag.Move:
                    W.SetWindowPos(bTop, IntPtr.Zero, dragRect.Left + (bx - dragX), dragRect.Top + (by - dragY), 0, 0,
                        W.SWP_NOSIZE | W.SWP_NOZORDER | W.SWP_NOACTIVATE);
                    break;
                case Drag.Resize: DoResize(); break;
                case Drag.BorrowPending:
                    if (Math.Abs(bx - dragX) > 4 || Math.Abs(by - dragY) > 4)
                    {
                        // turn into a real drag: real cursor follows B until release
                        BorrowSave();
                        SendMouse(dragX, dragY, 0, 0);
                        SendMouse(dragX, dragY, DownFlag(heldButton), XData(heldButton));
                        SendMouse(bx, by, 0, 0);
                        drag = Drag.BorrowDrag;
                    }
                    break;
                case Drag.BorrowDrag: SendMouse(bx, by, 0, 0); break;
                case Drag.Post:
                    if (W.IsWindow(pressedHwnd)) PostMouse(pressedHwnd, W.WM_MOUSEMOVE, MouseKeys(), bx, by);
                    break;
                case Drag.None:
                    {
                        IntPtr h = HitWindow();
                        if (h != IntPtr.Zero && !NeedsBorrow(h)) PostMouse(h, W.WM_MOUSEMOVE, MouseKeys(), bx, by);
                    }
                    break;
            }
        }

        void OnDown(int btn)
        {
            if (btn < 0 || btn > 4) return;
            if (drag != Drag.None)
            {
                // second button while one is held: only meaningful for posted input
                if (drag == Drag.Post && W.IsWindow(pressedHwnd))
                {
                    buttonMask |= MkOf(btn);
                    PostMouse(pressedHwnd, DownMsg(btn), MouseKeys() | XHi(btn), bx, by);
                }
                return;
            }

            IntPtr hwnd = HitWindow();
            if (hwnd == IntPtr.Zero) return;
            IntPtr top = W.GetAncestor(hwnd, W.GA_ROOT);
            if (top == IntPtr.Zero) top = hwnd;
            heldButton = btn;

            if (BorrowMode || NeedsBorrow(hwnd))
            {
                bTop = top; bFocus = hwnd;
                drag = Drag.BorrowPending; dragX = bx; dragY = by;
                return;
            }

            if (btn == 0)
            {
                int hit = HitTest(hwnd);
                if (hit == W.HTCAPTION)
                {
                    SetTarget(top, hwnd, false);
                    uint now = (uint)Environment.TickCount;
                    if (now - lastCapTick <= W.GetDoubleClickTime() && Near(lastCapX, lastCapY))
                    {
                        W.PostMessage(top, W.WM_SYSCOMMAND, new IntPtr(W.IsZoomed(top) ? W.SC_RESTORE : W.SC_MAXIMIZE), IntPtr.Zero);
                        lastCapTick = 0;
                        drag = Drag.NcButton; dragHit = 0;
                        return;
                    }
                    lastCapTick = now; lastCapX = bx; lastCapY = by;
                    if (W.IsZoomed(top)) { drag = Drag.NcButton; dragHit = 0; return; }
                    W.GetWindowRect(top, out dragRect); dragX = bx; dragY = by;
                    drag = Drag.Move;
                    return;
                }
                if (hit >= W.HTLEFT && hit <= W.HTBOTTOMRIGHT)
                {
                    SetTarget(top, hwnd, false);
                    W.GetWindowRect(top, out dragRect); dragX = bx; dragY = by; dragHit = hit;
                    drag = Drag.Resize;
                    return;
                }
                if (hit == W.HTCLOSE || hit == W.HTMINBUTTON || hit == W.HTMAXBUTTON)
                {
                    SetTarget(top, hwnd, false);
                    drag = Drag.NcButton; dragHit = hit;
                    return;
                }
                if (hit != W.HTCLIENT && hit != 0)
                {
                    // menu bar, scroll bar, system menu ... these run modal loops on the real cursor
                    bTop = top; bFocus = hwnd;
                    drag = Drag.BorrowPending; dragX = bx; dragY = by;
                    return;
                }
            }

            SetTarget(top, hwnd, true);
            int msg = DownMsg(btn);
            uint t = (uint)Environment.TickCount;
            if (btn == lastDownBtn && hwnd == lastDownHwnd && t - lastDownTick <= W.GetDoubleClickTime()
                && Near(lastDownX, lastDownY) && (W.ClassStyle(hwnd) & W.CS_DBLCLKS) != 0)
            {
                msg = DblMsg(btn);
                lastDownTick = 0;
            }
            else { lastDownTick = t; lastDownX = bx; lastDownY = by; lastDownBtn = btn; lastDownHwnd = hwnd; }

            buttonMask |= MkOf(btn);
            pressedHwnd = hwnd;
            drag = Drag.Post;
            PostMouse(hwnd, msg, MouseKeys() | XHi(btn), bx, by);
        }

        void OnUp(int btn)
        {
            if (btn < 0 || btn > 4) return;
            switch (drag)
            {
                case Drag.Post:
                    if ((buttonMask & MkOf(btn)) == 0) return;
                    buttonMask &= ~MkOf(btn);
                    if (W.IsWindow(pressedHwnd)) PostMouse(pressedHwnd, UpMsg(btn), MouseKeys() | XHi(btn), bx, by);
                    if (buttonMask == 0) { drag = Drag.None; pressedHwnd = IntPtr.Zero; heldButton = -1; }
                    return;
                case Drag.NcButton:
                    if (btn != heldButton) return;
                    if (dragHit != 0 && W.IsWindow(bTop))
                    {
                        IntPtr h = HitWindow();
                        if (h != IntPtr.Zero && HitTest(h) == dragHit)
                        {
                            int sc = dragHit == W.HTCLOSE ? W.SC_CLOSE : dragHit == W.HTMINBUTTON ? W.SC_MINIMIZE
                                : (W.IsZoomed(bTop) ? W.SC_RESTORE : W.SC_MAXIMIZE);
                            W.PostMessage(bTop, W.WM_SYSCOMMAND, new IntPtr(sc), IntPtr.Zero);
                        }
                    }
                    break;
                case Drag.BorrowPending:
                    if (btn != heldButton) return;
                    BorrowSave();
                    SendMouse(bx, by, 0, 0);
                    SendMouse(bx, by, DownFlag(btn), XData(btn));
                    SendMouse(bx, by, UpFlag(btn), XData(btn));
                    BorrowRestore();
                    break;
                case Drag.BorrowDrag:
                    if (btn != heldButton) return;
                    SendMouse(bx, by, UpFlag(btn), XData(btn));
                    BorrowRestore();
                    break;
                case Drag.Move:
                case Drag.Resize:
                    if (btn != heldButton) return;
                    break;
                default: return;
            }
            drag = Drag.None; heldButton = -1; buttonMask = 0; pressedHwnd = IntPtr.Zero;
        }

        void OnWheel(int delta, bool horizontal)
        {
            IntPtr h = HitWindow();
            if (h == IntPtr.Zero) return;
            if (BorrowMode || NeedsBorrow(h))
            {
                BorrowSave();
                SendMouse(bx, by, 0, 0);
                SendMouse(bx, by, horizontal ? W.MOUSEEVENTF_HWHEEL : W.MOUSEEVENTF_WHEEL, (uint)delta);
                BorrowRestore();
                return;
            }
            POINT p = LogicalScreen(h, bx, by);
            IntPtr wp = new IntPtr((int)((uint)(ushort)MouseKeys() | ((uint)(ushort)(short)delta << 16)));
            W.PostMessage(h, horizontal ? W.WM_MOUSEHWHEEL : W.WM_MOUSEWHEEL, wp, W.MakeLParam(p.X, p.Y));
        }

        void DoResize()
        {
            int dx = bx - dragX, dy = by - dragY;
            RECT r = dragRect;
            if (dragHit == W.HTLEFT || dragHit == W.HTTOPLEFT || dragHit == W.HTBOTTOMLEFT) r.Left += dx;
            if (dragHit == W.HTRIGHT || dragHit == W.HTTOPRIGHT || dragHit == W.HTBOTTOMRIGHT) r.Right += dx;
            if (dragHit == W.HTTOP || dragHit == W.HTTOPLEFT || dragHit == W.HTTOPRIGHT) r.Top += dy;
            if (dragHit == W.HTBOTTOM || dragHit == W.HTBOTTOMLEFT || dragHit == W.HTBOTTOMRIGHT) r.Bottom += dy;
            if (r.Width < 120 || r.Height < 60) return;
            W.SetWindowPos(bTop, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, W.SWP_NOZORDER | W.SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------------ borrow (real cursor for a moment)
        POINT savedA; IntPtr savedFg;

        void BorrowSave()
        {
            W.GetCursorPos(out savedA);
            savedFg = W.GetForegroundWindow();
        }

        void BorrowRestore()
        {
            SendMouse(savedA.X, savedA.Y, 0, 0);
            Thread.Sleep(60);
            IntPtr fg = W.GetForegroundWindow();
            if (fg == savedFg || savedFg == IntPtr.Zero || !W.IsWindow(savedFg)) return;
            // keep a menu/popup B just opened, otherwise hand the active window back to A
            GUITHREADINFO gi = new GUITHREADINFO();
            gi.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (W.GetGUIThreadInfo(W.ThreadOf(fg), ref gi) && (gi.flags & (W.GUI_INMENUMODE | W.GUI_POPUPMENUMODE)) != 0) return;
            string cls = W.ClassOf(fg);
            if (cls == "#32768" || cls.StartsWith("Xaml_WindowedPopup")) return;
            if (W.ProcessOf(fg) == W.ProcessOf(bTop) && fg != bTop && IsPopupLike(fg)) return;
            W.SetForegroundWindow(savedFg);
        }

        static bool IsPopupLike(IntPtr h)
        {
            RECT r; W.GetWindowRect(h, out r);
            return r.Width < 900 && r.Height < 900; // small window that appeared after the click (dropdown, menu)
        }

        void SendMouse(int x, int y, uint flags, uint data)
        {
            int vx = W.GetSystemMetrics(W.SM_XVIRTUALSCREEN), vy = W.GetSystemMetrics(W.SM_YVIRTUALSCREEN);
            int vw = W.GetSystemMetrics(W.SM_CXVIRTUALSCREEN), vh = W.GetSystemMetrics(W.SM_CYVIRTUALSCREEN);
            INPUT[] inp = new INPUT[1];
            inp[0].type = W.INPUT_MOUSE;
            inp[0].u.mi.dx = (int)(((long)(x - vx) * 65535 + (vw - 1) / 2) / Math.Max(1, vw - 1));
            inp[0].u.mi.dy = (int)(((long)(y - vy) * 65535 + (vh - 1) / 2) / Math.Max(1, vh - 1));
            inp[0].u.mi.mouseData = data;
            inp[0].u.mi.dwFlags = flags | W.MOUSEEVENTF_MOVE | W.MOUSEEVENTF_ABSOLUTE | W.MOUSEEVENTF_VIRTUALDESK;
            W.SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        static uint DownFlag(int b) { return b == 0 ? W.MOUSEEVENTF_LEFTDOWN : b == 1 ? W.MOUSEEVENTF_RIGHTDOWN : b == 2 ? W.MOUSEEVENTF_MIDDLEDOWN : W.MOUSEEVENTF_XDOWN; }
        static uint UpFlag(int b) { return b == 0 ? W.MOUSEEVENTF_LEFTUP : b == 1 ? W.MOUSEEVENTF_RIGHTUP : b == 2 ? W.MOUSEEVENTF_MIDDLEUP : W.MOUSEEVENTF_XUP; }
        static uint XData(int b) { return b == 3 ? 1u : b == 4 ? 2u : 0u; }

        // ------------------------------------------------------------------ keyboard
        void OnKey(int vk, int scan, bool ext, bool down)
        {
            if (vk <= 0 || vk > 255) return;
            if (vk == W.VK_SHIFT) vk = W.VK_LSHIFT;
            else if (vk == W.VK_CONTROL) vk = W.VK_LCONTROL;
            else if (vk == W.VK_MENU) vk = W.VK_LMENU;
            if (!down && !keyDown[vk]) return;            // up for a key we never saw go down
            bool wasDown = keyDown[vk];
            keyDown[vk] = down;
            if (down && vk == W.VK_CAPITAL && !wasDown) caps = !caps;
            Fold();

            bool ctrl = keyDown[W.VK_CONTROL], alt = keyDown[W.VK_MENU];
            bool win = keyDown[W.VK_LWIN] || keyDown[W.VK_RWIN];

            // keys that must not escape to the whole PC
            if (vk == W.VK_LWIN || vk == W.VK_RWIN || win) return;
            if (down && alt && (vk == W.VK_TAB || vk == W.VK_ESCAPE)) return;
            if (down && ctrl && vk == W.VK_ESCAPE) return;
            if (down && alt && !ctrl && vk == W.VK_F4)
            {
                if (W.IsWindow(bTop)) W.PostMessage(bTop, W.WM_SYSCOMMAND, new IntPtr(W.SC_CLOSE), IntPtr.Zero);
                return;
            }

            IntPtr target = ResolveFocus();
            if (target == IntPtr.Zero) return;
            bool isMod = Generic(vk) == W.VK_SHIFT || Generic(vk) == W.VK_CONTROL || Generic(vk) == W.VK_MENU;
            // B pressed/let go of Shift/Ctrl/Alt: first let B's app finish the keys already sent,
            // so each key is turned into a character with the modifiers that were held for it.
            if (isMod && attachedThread != 0) WaitIdle(target);
            bool synced = SyncKeyState(target, vk);
            if (vk == W.VK_MENU || vk == W.VK_LMENU || vk == W.VK_RMENU) return; // Alt only acts as a modifier

            int msgVk = Generic(vk);
            bool sys = alt && !ctrl;
            int lp = 1 | ((scan & 0xFF) << 16) | (ext ? 1 << 24 : 0) | (alt ? 1 << 29 : 0);
            if (down) { if (wasDown) lp |= 1 << 30; }
            else lp |= (1 << 30) | (1 << 31);

            // Plain text (letters, digits, symbols, with or without Shift/AltGr): hand the app the
            // finished character. No key-down, so the app never has to look up Shift itself
            // (that lookup is what races when B types fast) and nothing gets doubled.
            if (!down && charOnly[vk]) { charOnly[vk] = false; return; }
            bool shortcut = (ctrl && !alt) || (alt && !ctrl);
            if (down && !shortcut)
            {
                string chars = Translate(vk, scan, target);
                if (chars.Length > 0 && chars[0] >= 0x20)
                {
                    charOnly[vk] = true;
                    Settle(target);
                    foreach (char ch in chars)
                        W.PostMessage(target, W.WM_CHAR, new IntPtr(ch), new IntPtr(lp));
                    return;
                }
            }

            // Everything else (Enter, Backspace, arrows, F-keys, Ctrl+C, Alt+F ...) goes as a real key.
            int kmsg = down ? (sys ? W.WM_SYSKEYDOWN : W.WM_KEYDOWN) : (sys ? W.WM_SYSKEYUP : W.WM_KEYUP);
            if (synced || !shortcut)
            {
                // synced: B's app sees B's own Ctrl/Shift/Alt, so shortcuts behave like real typing
                W.PostMessage(target, kmsg, new IntPtr(msgVk), new IntPtr(lp));
                if (down && Translate(vk, scan, target).Length > 0) pendingTranslate = target;
                return;
            }
            // B's window belongs to the same app Player A is typing in (e.g. both in Chrome).
            // For a shortcut, lend B's modifiers to that app for a moment, then put A's back.
            if (!down) { W.PostMessage(target, kmsg, new IntPtr(msgVk), new IntPtr(lp)); return; }
            BorrowedShortcut(target, kmsg, msgVk, lp);
        }

        readonly bool[] charOnly = new bool[256];

        // An Enter/Backspace/Tab key-down becomes a character only when B's app gets to it
        // (TranslateMessage). Characters we post directly must not overtake it.
        IntPtr pendingTranslate;
        void Settle(IntPtr target)
        {
            if (pendingTranslate == IntPtr.Zero) return;
            WaitIdle(pendingTranslate);
            W.SendTimeout(pendingTranslate, W.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(8);
            W.SendTimeout(pendingTranslate, W.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            pendingTranslate = IntPtr.Zero;
        }

        void BorrowedShortcut(IntPtr target, int kmsg, int msgVk, int lp)
        {
            uint tid = W.ThreadOf(target);
            if (tid == 0 || tid == myThread) return;
            Detach();
            if (!W.AttachThreadInput(myThread, tid, true)) { W.PostMessage(target, kmsg, new IntPtr(msgVk), new IntPtr(lp)); return; }
            try
            {
                byte[] s = new byte[256];
                W.GetKeyboardState(s);
                int[] mods = { W.VK_SHIFT, W.VK_LSHIFT, W.VK_RSHIFT, W.VK_CONTROL, W.VK_LCONTROL, W.VK_RCONTROL, W.VK_MENU, W.VK_LMENU, W.VK_RMENU };
                foreach (int m in mods) s[m] = (byte)((s[m] & 1) | (keyDown[m] ? 0x80 : 0));
                W.SetKeyboardState(s);
                W.PostMessage(target, kmsg, new IntPtr(msgVk), new IntPtr(lp));
                W.SendTimeout(target, W.WM_NULL, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(40);
                W.SendTimeout(target, W.WM_NULL, IntPtr.Zero, IntPtr.Zero);
                // back to whatever Player A is physically holding
                W.GetKeyboardState(s);
                foreach (int m in mods) s[m] = (byte)((s[m] & 1) | ((W.GetAsyncKeyState(m) & 0x8000) != 0 ? 0x80 : 0));
                W.SetKeyboardState(s);
            }
            finally { W.AttachThreadInput(myThread, tid, false); }
        }

        // Phone keyboards send finished text, not key presses.
        void TypeChar(int code, int mods)
        {
            if (code == '\n' || code == '\r') { Tap(0x0D, mods); return; }
            if (code == '\b') { Tap(0x08, mods); return; }
            if (code == '\t') { Tap(0x09, mods); return; }
            if (code < 0x20) return;
            if (mods == 0)
            {
                IntPtr target = ResolveFocus();
                if (target == IntPtr.Zero) return;
                string s;
                try { s = char.ConvertFromUtf32(code); } catch { return; }
                Settle(target);
                foreach (char ch in s) W.PostMessage(target, W.WM_CHAR, new IntPtr(ch), new IntPtr(1));
                return;
            }
            if (code > 0xFFFF) return;
            short r = W.VkKeyScanEx((char)code, W.GetKeyboardLayout(W.ThreadOf(bTop)));
            if (r == -1) return;
            if (((r >> 8) & 1) != 0) mods |= 4;
            Tap(r & 0xFF, mods);
        }

        // press + release one key, with Ctrl/Alt/Shift held around it
        public void Tap(int vk, int mods)
        {
            int[] mv = { W.VK_LCONTROL, W.VK_LMENU, W.VK_LSHIFT };
            for (int i = 0; i < 3; i++) if ((mods & (1 << i)) != 0) OnKey(mv[i], ScanOf(mv[i]), false, true);
            OnKey(vk, ScanOf(vk), IsExtended(vk), true);
            OnKey(vk, ScanOf(vk), IsExtended(vk), false);
            for (int i = 2; i >= 0; i--) if ((mods & (1 << i)) != 0) OnKey(mv[i], ScanOf(mv[i]), false, false);
        }

        public static int ScanOf(int vk) { return (int)W.MapVirtualKey((uint)vk, 0); }
        public static bool IsExtended(int vk) { return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == W.VK_LWIN || vk == W.VK_RWIN || vk == W.VK_RCONTROL || vk == W.VK_RMENU; }

        void Fold()
        {
            // the client's keyboard hook always reports left/right variants
            keyDown[W.VK_SHIFT] = keyDown[W.VK_LSHIFT] || keyDown[W.VK_RSHIFT];
            keyDown[W.VK_CONTROL] = keyDown[W.VK_LCONTROL] || keyDown[W.VK_RCONTROL];
            keyDown[W.VK_MENU] = keyDown[W.VK_LMENU] || keyDown[W.VK_RMENU];
        }

        static int Generic(int vk)
        {
            if (vk == W.VK_LSHIFT || vk == W.VK_RSHIFT) return W.VK_SHIFT;
            if (vk == W.VK_LCONTROL || vk == W.VK_RCONTROL) return W.VK_CONTROL;
            if (vk == W.VK_LMENU || vk == W.VK_RMENU) return W.VK_MENU;
            return vk;
        }

        byte[] StateArray()
        {
            byte[] s = new byte[256];
            for (int i = 0; i < 256; i++) if (keyDown[i]) s[i] = 0x80;
            if (caps) s[W.VK_CAPITAL] |= 1;
            return s;
        }

        string Translate(int vk, int scan, IntPtr target)
        {
            byte[] s = StateArray();
            IntPtr hkl = W.GetKeyboardLayout(W.ThreadOf(target));
            StringBuilder sb = new StringBuilder(8);
            int n = W.ToUnicodeEx((uint)vk, (uint)scan, s, sb, 8, 4 /* don't touch dead-key state */, hkl);
            if (n <= 0) return "";
            string r = sb.ToString(0, Math.Min(n, sb.Length));
            if (keyDown[W.VK_MENU] && !keyDown[W.VK_CONTROL]) return r.ToLowerInvariant();
            return r;
        }

        IntPtr ResolveFocus()
        {
            if (!W.IsWindow(bTop)) return IntPtr.Zero;
            uint tid = W.ThreadOf(bTop);
            if (tid != W.ThreadOf(W.GetForegroundWindow()))
            {
                GUITHREADINFO gi = new GUITHREADINFO();
                gi.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                if (W.GetGUIThreadInfo(tid, ref gi) && gi.hwndFocus != IntPtr.Zero &&
                    (gi.hwndFocus == bTop || W.IsChild(bTop, gi.hwndFocus)))
                    return gi.hwndFocus;
            }
            return W.IsWindow(bFocus) ? bFocus : bTop;
        }

        // Share B's modifier state (Ctrl/Shift/Alt) with B's app only, so its shortcuts work,
        // without touching Player A's app (which runs on a different input queue).
        bool SyncKeyState(IntPtr target, int vk)
        {
            uint tid = W.ThreadOf(target);
            if (tid == 0) return false;
            if (tid == W.ThreadOf(W.GetForegroundWindow())) { Detach(); return false; }
            if (!EnsureAttached(tid)) return false;
            byte[] s = new byte[256];
            W.GetKeyboardState(s);
            int[] mods = { W.VK_SHIFT, W.VK_LSHIFT, W.VK_RSHIFT, W.VK_CONTROL, W.VK_LCONTROL, W.VK_RCONTROL,
                           W.VK_MENU, W.VK_LMENU, W.VK_RMENU };
            foreach (int m in mods) s[m] = (byte)((s[m] & 1) | (keyDown[m] ? 0x80 : 0));
            s[vk] = (byte)((s[vk] & 1) | (keyDown[vk] ? 0x80 : 0));
            s[W.VK_CAPITAL] = (byte)((s[W.VK_CAPITAL] & 0x80) | (caps ? 1 : 0));
            return W.SetKeyboardState(s);
        }

        uint idlePid; IntPtr idleHandle;

        void WaitIdle(IntPtr target)
        {
            uint pid = W.ProcessOf(target);
            if (pid != idlePid)
            {
                if (idleHandle != IntPtr.Zero) W.CloseHandle(idleHandle);
                idleHandle = W.OpenProcess(0x00100000 /*SYNCHRONIZE*/ | 0x1000 /*QUERY_LIMITED*/, false, pid);
                idlePid = pid;
            }
            if (idleHandle == IntPtr.Zero || W.WaitForInputIdle(idleHandle, 200) != 0) Thread.Sleep(25);
        }

        bool EnsureAttached(uint tid)
        {
            if (tid == myThread) return false;
            if (attachedThread == tid) return true;
            Detach();
            if (W.AttachThreadInput(myThread, tid, true)) { attachedThread = tid; return true; }
            return false;
        }

        void Detach()
        {
            if (attachedThread != 0)
            {
                W.AttachThreadInput(myThread, attachedThread, false);
                attachedThread = 0;
            }
        }

        // ------------------------------------------------------------------ helpers
        void SetTarget(IntPtr top, IntPtr hwnd, bool focus)
        {
            bool changed = top != bTop;
            bTop = top; bFocus = hwnd;
            if (changed)
                W.SetWindowPos(top, W.HWND_TOP, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOACTIVATE | W.SWP_NOOWNERZORDER);
            if (!focus) return;
            uint tid = W.ThreadOf(hwnd);
            if (tid == 0 || tid == W.ThreadOf(W.GetForegroundWindow())) return; // same app as A: leave A's focus alone
            if (EnsureAttached(tid)) W.SetFocus(hwnd);
        }

        public void ReleaseAll()
        {
            if (drag == Drag.BorrowDrag) { SendMouse(bx, by, UpFlag(heldButton), XData(heldButton)); BorrowRestore(); }
            else if (drag == Drag.Post && W.IsWindow(pressedHwnd))
            {
                for (int b = 0; b < 5; b++)
                    if ((buttonMask & MkOf(b)) != 0) PostMouse(pressedHwnd, UpMsg(b), XHi(b), bx, by);
            }
            drag = Drag.None; heldButton = -1; buttonMask = 0; pressedHwnd = IntPtr.Zero;

            IntPtr target = ResolveFocus();
            for (int vk = 1; vk < 256; vk++)
            {
                if (!keyDown[vk]) continue;
                keyDown[vk] = false;
                if (target != IntPtr.Zero && vk != W.VK_LWIN && vk != W.VK_RWIN && Generic(vk) != W.VK_MENU
                    && vk != W.VK_SHIFT && vk != W.VK_CONTROL)
                    W.PostMessage(target, W.WM_KEYUP, new IntPtr(Generic(vk)), new IntPtr(1 | (3 << 30)));
            }
            if (target != IntPtr.Zero && attachedThread != 0) SyncKeyState(target, 0);
            Detach();
        }

        bool Near(int x, int y)
        {
            return Math.Abs(bx - x) <= W.GetSystemMetrics(W.SM_CXDOUBLECLK) && Math.Abs(by - y) <= W.GetSystemMetrics(W.SM_CYDOUBLECLK);
        }

        IntPtr HitWindow()
        {
            IntPtr h = W.WindowFromPoint(new POINT(bx, by));
            if (h == IntPtr.Zero) return h;
            if (W.ProcessOf(h) == myPid) return IntPtr.Zero; // never act on DuoSeat's own windows
            return h;
        }

        bool NeedsBorrow(IntPtr h)
        {
            if (BorrowClasses.Contains(W.ClassOf(h))) return true;
            IntPtr top = W.GetAncestor(h, W.GA_ROOT);
            return top != IntPtr.Zero && BorrowClasses.Contains(W.ClassOf(top));
        }

        int HitTest(IntPtr h)
        {
            POINT p = LogicalScreen(h, bx, by);
            return W.SendTimeout(h, W.WM_NCHITTEST, IntPtr.Zero, W.MakeLParam(p.X, p.Y));
        }

        int MouseKeys()
        {
            int k = buttonMask;
            if (keyDown[W.VK_SHIFT]) k |= W.MK_SHIFT;
            if (keyDown[W.VK_CONTROL]) k |= W.MK_CONTROL;
            return k;
        }

        static int MkOf(int b) { return b == 0 ? W.MK_LBUTTON : b == 1 ? W.MK_RBUTTON : b == 2 ? W.MK_MBUTTON : b == 3 ? W.MK_XBUTTON1 : W.MK_XBUTTON2; }
        static int XHi(int b) { return b == 3 ? 1 << 16 : b == 4 ? 2 << 16 : 0; }
        static int DownMsg(int b) { return b == 0 ? W.WM_LBUTTONDOWN : b == 1 ? W.WM_RBUTTONDOWN : b == 2 ? W.WM_MBUTTONDOWN : W.WM_XBUTTONDOWN; }
        static int UpMsg(int b) { return b == 0 ? W.WM_LBUTTONUP : b == 1 ? W.WM_RBUTTONUP : b == 2 ? W.WM_MBUTTONUP : W.WM_XBUTTONUP; }
        static int DblMsg(int b) { return b == 0 ? W.WM_LBUTTONDBLCLK : b == 1 ? W.WM_RBUTTONDBLCLK : b == 2 ? W.WM_MBUTTONDBLCLK : W.WM_XBUTTONDBLCLK; }

        // Convert a physical screen point into the coordinate space the target window lives in.
        static POINT LogicalScreen(IntPtr h, int x, int y)
        {
            POINT p = new POINT(x, y);
            try { W.PhysicalToLogicalPointForPerMonitorDPI(h, ref p); } catch { }
            return p;
        }

        static void PostMouse(IntPtr h, int msg, int wParam, int x, int y)
        {
            POINT p = LogicalScreen(h, x, y);
            IntPtr old = IntPtr.Zero; bool switched = false;
            try { old = W.SetThreadDpiAwarenessContext(W.GetWindowDpiAwarenessContext(h)); switched = old != IntPtr.Zero; } catch { }
            W.ScreenToClient(h, ref p);
            if (switched) { try { W.SetThreadDpiAwarenessContext(old); } catch { } }
            W.PostMessage(h, msg, new IntPtr(wParam), W.MakeLParam(p.X, p.Y));
        }
    }
}
