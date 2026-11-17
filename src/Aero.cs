// Windows 7 "Aero"-era look: blue gradient header, glossy buttons, command links.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DuoSeat
{
    static class Aero
    {
        public static readonly Color Ink = Color.FromArgb(0x1E, 0x32, 0x87);      // Win7 control-panel heading blue
        public static readonly Color LinkInk = Color.FromArgb(0x15, 0x1C, 0x55);
        public static readonly Font Body = new Font("Segoe UI", 9f);

        static Icon appIcon;
        public static Icon AppIcon
        {
            get
            {
                if (appIcon == null) { try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } }
                return appIcon;
            }
        }

        // Adds the gradient header + grey footer strip, pushes the existing controls below the header.
        public static void Dress(Form f, string title, string subtitle, int footerHeight)
        {
            f.BackColor = Color.White;
            f.Font = Body;
            if (AppIcon != null) f.Icon = AppIcon;
            int hh = 66;
            foreach (Control c in f.Controls) c.Top += hh;
            f.ClientSize = new Size(f.ClientSize.Width, f.ClientSize.Height + hh + footerHeight);

            Panel head = new Panel { Bounds = new Rectangle(0, 0, f.ClientSize.Width, hh), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            head.Paint += delegate (object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Rectangle r = head.ClientRectangle;
                using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xD5, 0xE5, 0xF7), 90f))
                    g.FillRectangle(b, r);
                using (Pen p = new Pen(Color.FromArgb(0x9A, 0xB8, 0xDE))) g.DrawLine(p, 0, r.Bottom - 1, r.Right, r.Bottom - 1);
                float k = head.DeviceDpi / 96f;
                if (AppIcon != null)
                    using (Icon big = new Icon(AppIcon, (int)(48 * k), (int)(48 * k)))
                        g.DrawIcon(big, new Rectangle((int)(12 * k), (int)(9 * k), (int)(48 * k), (int)(48 * k)));
                using (Font tf = new Font("Segoe UI", 14f))
                    TextRenderer.DrawText(g, title, tf, new Point((int)(70 * k), (int)(8 * k)), Ink);
                TextRenderer.DrawText(g, subtitle, Body, new Point((int)(72 * k), (int)(38 * k)), Color.FromArgb(0x44, 0x44, 0x44));
            };
            f.Controls.Add(head);

            if (footerHeight > 0)
            {
                Panel foot = new Panel
                {
                    Bounds = new Rectangle(0, f.ClientSize.Height - footerHeight, f.ClientSize.Width, footerHeight),
                    BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0),
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                    Name = "footer"
                };
                foot.Paint += delegate (object s, PaintEventArgs e)
                {
                    using (Pen p = new Pen(Color.FromArgb(0xDF, 0xDF, 0xDF))) e.Graphics.DrawLine(p, 0, 0, foot.Width, 0);
                };
                f.Controls.Add(foot);
            }

            foreach (Control c in f.Controls) SkinTree(c);
        }

        static void SkinTree(Control c)
        {
            Button b = c as Button;
            if (b != null && b.Tag as string != "link") Skin(b);
            foreach (Control k in c.Controls) SkinTree(k);
        }

        // Glossy Win7 push button.
        public static void Skin(Button b)
        {
            bool hot = false, pressed = false;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.Transparent;
            b.MouseEnter += delegate { hot = true; b.Invalidate(); };
            b.MouseLeave += delegate { hot = false; pressed = false; b.Invalidate(); };
            b.MouseDown += delegate { pressed = true; b.Invalidate(); };
            b.MouseUp += delegate { pressed = false; b.Invalidate(); };
            b.EnabledChanged += delegate { b.Invalidate(); };
            b.Paint += delegate (object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Rectangle r = new Rectangle(0, 0, b.Width - 1, b.Height - 1);
                g.Clear(b.Parent != null ? b.Parent.BackColor : Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color t1, t2, b1, b2, edge;
                if (!b.Enabled) { t1 = t2 = b1 = b2 = Color.FromArgb(0xF4, 0xF4, 0xF4); edge = Color.FromArgb(0xAD, 0xB2, 0xB5); }
                else if (pressed) { t1 = Color.FromArgb(0xE5, 0xF4, 0xFC); t2 = Color.FromArgb(0xC4, 0xE5, 0xF6); b1 = Color.FromArgb(0x98, 0xD1, 0xEF); b2 = Color.FromArgb(0x68, 0xB3, 0xDB); edge = Color.FromArgb(0x2C, 0x62, 0x8B); }
                else if (hot) { t1 = Color.FromArgb(0xEA, 0xF6, 0xFD); t2 = Color.FromArgb(0xD9, 0xF0, 0xFC); b1 = Color.FromArgb(0xBE, 0xE6, 0xFD); b2 = Color.FromArgb(0xA7, 0xD9, 0xF5); edge = Color.FromArgb(0x3C, 0x7F, 0xB1); }
                else { t1 = Color.FromArgb(0xF2, 0xF2, 0xF2); t2 = Color.FromArgb(0xEB, 0xEB, 0xEB); b1 = Color.FromArgb(0xDD, 0xDD, 0xDD); b2 = Color.FromArgb(0xCF, 0xCF, 0xCF); edge = Color.FromArgb(0x70, 0x70, 0x70); }
                using (GraphicsPath path = Round(r, 3))
                {
                    Rectangle top = new Rectangle(r.X, r.Y, r.Width, r.Height / 2);
                    Rectangle bot = new Rectangle(r.X, r.Y + r.Height / 2, r.Width, r.Height - r.Height / 2);
                    g.SetClip(path);
                    using (LinearGradientBrush tb = new LinearGradientBrush(new Rectangle(top.X, top.Y, top.Width, top.Height + 1), t1, t2, 90f)) g.FillRectangle(tb, top);
                    using (LinearGradientBrush bb = new LinearGradientBrush(new Rectangle(bot.X, bot.Y - 1, bot.Width, bot.Height + 1), b1, b2, 90f)) g.FillRectangle(bb, bot);
                    g.ResetClip();
                    using (Pen p = new Pen(edge)) g.DrawPath(p, path);
                    using (Pen p = new Pen(Color.FromArgb(150, Color.White))) g.DrawPath(p, Round(Rectangle.Inflate(r, -1, -1), 2));
                }
                TextRenderer.DrawText(g, b.Text, b.Font, r, b.Enabled ? Color.Black : Color.FromArgb(0x83, 0x83, 0x83),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            };
        }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Win7 "command link": green arrow, big blue heading, grey note, light-blue hover box.
        public static Button CommandLink(string head, string note, Rectangle bounds)
        {
            Button b = new Button { Bounds = bounds, FlatStyle = FlatStyle.Flat, Tag = "link", Cursor = Cursors.Hand, Text = head, BackColor = Color.White };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.White;
            b.FlatAppearance.MouseDownBackColor = Color.White;
            bool hot = false;
            b.MouseEnter += delegate { hot = true; b.Invalidate(); };
            b.MouseLeave += delegate { hot = false; b.Invalidate(); };
            b.Paint += delegate (object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float k = b.DeviceDpi / 96f;
                if (hot)
                {
                    Rectangle r = new Rectangle(0, 0, b.Width - 1, b.Height - 1);
                    using (GraphicsPath p = Round(r, 3))
                    {
                        using (LinearGradientBrush lb = new LinearGradientBrush(r, Color.FromArgb(0xF7, 0xFB, 0xFF), Color.FromArgb(0xE5, 0xF3, 0xFB), 90f)) g.FillPath(lb, p);
                        using (Pen pen = new Pen(Color.FromArgb(0xC6, 0xE2, 0xF7))) g.DrawPath(pen, p);
                    }
                }
                // green arrow
                float ax = 12 * k, ay = 14 * k;
                PointF[] arrow = {
                    new PointF(ax, ay + 5 * k), new PointF(ax + 9 * k, ay + 5 * k), new PointF(ax + 9 * k, ay),
                    new PointF(ax + 17 * k, ay + 8 * k), new PointF(ax + 9 * k, ay + 16 * k), new PointF(ax + 9 * k, ay + 11 * k),
                    new PointF(ax, ay + 11 * k)
                };
                using (LinearGradientBrush gb = new LinearGradientBrush(new RectangleF(ax, ay, 18 * k, 17 * k), Color.FromArgb(0x5C, 0xC8, 0x3D), Color.FromArgb(0x1E, 0x8A, 0x16), 90f))
                    g.FillPolygon(gb, arrow);
                using (Font hf = new Font("Segoe UI", 12f))
                    TextRenderer.DrawText(g, head, hf, new Point((int)(38 * k), (int)(8 * k)), LinkInk);
                TextRenderer.DrawText(g, note, Body, new Rectangle((int)(40 * k), (int)(34 * k), b.Width - (int)(48 * k), b.Height - (int)(36 * k)),
                    Color.FromArgb(0x44, 0x44, 0x44), TextFormatFlags.WordBreak);
            };
            return b;
        }
    }
}
