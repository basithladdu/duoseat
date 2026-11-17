// Player B's live view of A's screen (or the virtual screen made for B).
// Your mouse over the picture drives your orange cursor over there; your keys go there
// while this window is in front. Player A's own cursor and typing are never touched.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace DuoSeat
{
    class ViewerForm : Form
    {
        readonly ClientForm owner;
        Bitmap frame;
        int lastNx = -1, lastNy = -1;

        public ViewerForm(ClientForm owner)
        {
            this.owner = owner;
            Text = "DuoSeat - live view (your cursor is the orange one)";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(1280, wa.Width - 80), Math.Min(760, wa.Height - 80));
            if (Aero.AppIcon != null) Icon = Aero.AppIcon;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            KeyPreview = true;
        }

        // called on the network thread
        public void PushFrame(byte[] jpg)
        {
            Bitmap b = null;
            try
            {
                using (MemoryStream ms = new MemoryStream(jpg))
                using (Image tmp = Image.FromStream(ms))
                    b = new Bitmap(tmp);
            }
            catch { }
            if (IsDisposed || !IsHandleCreated) { if (b != null) b.Dispose(); return; }
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (b != null)
                    {
                        Bitmap old = frame; frame = b;
                        if (old != null) old.Dispose();
                        Invalidate();
                    }
                    owner.Send(Proto.Ack, 0, 0, 0);
                });
            }
            catch { }
        }

        Rectangle Fit()
        {
            Rectangle c = ClientRectangle;
            if (frame == null || c.Width <= 0 || c.Height <= 0) return c;
            double s = Math.Min((double)c.Width / frame.Width, (double)c.Height / frame.Height);
            int w = (int)(frame.Width * s), h = (int)(frame.Height * s);
            return new Rectangle((c.Width - w) / 2, (c.Height - h) / 2, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            if (frame == null)
            {
                TextRenderer.DrawText(e.Graphics, "Waiting for Player A's screen...", Font, ClientRectangle, Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
            e.Graphics.DrawImage(frame, Fit());
        }

        bool ToNorm(Point p, out int nx, out int ny)
        {
            Rectangle r = Fit();
            nx = ny = 0;
            if (frame == null || r.Width <= 0) return false;
            nx = (int)Math.Round(Math.Max(0, Math.Min(1, (p.X - r.X) / (double)r.Width)) * 10000);
            ny = (int)Math.Round(Math.Max(0, Math.Min(1, (p.Y - r.Y) / (double)r.Height)) * 10000);
            return true;
        }

        void MoveTo(Point p)
        {
            int nx, ny;
            if (owner.Capturing || !ToNorm(p, out nx, out ny)) return;
            if (nx == lastNx && ny == lastNy) return;
            lastNx = nx; lastNy = ny;
            owner.Send(Proto.MoveAbs, nx, ny, 0);
        }

        static int Btn(MouseButtons b)
        {
            switch (b)
            {
                case MouseButtons.Right: return 1;
                case MouseButtons.Middle: return 2;
                case MouseButtons.XButton1: return 3;
                case MouseButtons.XButton2: return 4;
                default: return 0;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); MoveTo(e.Location); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (owner.Capturing) return;
            MoveTo(e.Location);
            owner.Send(Proto.Button, Btn(e.Button), 1, 0);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (owner.Capturing) return;
            MoveTo(e.Location);
            owner.Send(Proto.Button, Btn(e.Button), 0, 0);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (owner.Capturing) return;
            MoveTo(e.Location);
            owner.Send(Proto.Wheel, e.Delta, 0, 0);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (frame != null) { frame.Dispose(); frame = null; }
        }
    }
}
