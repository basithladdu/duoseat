// A plain window with a text box, used as Player A's / Player B's app in tests.
// usage: TestTarget.exe <title> <x> <y> <w> <h>
using System;
using System.Drawing;
using System.Windows.Forms;

static class TestTarget
{
    [STAThread]
    static void Main(string[] a)
    {
        DuoSeat.W.MakeDpiAware();
        Form f = new Form { Text = a[0], StartPosition = FormStartPosition.Manual };
        TextBox t = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 14f), Name = "box" };
        // shortcut handled like most apps do: by checking the Ctrl key state on key-down
        t.KeyDown += delegate (object s, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.A) { t.SelectAll(); e.SuppressKeyPress = true; } };
        f.Controls.Add(t);
        f.Load += delegate { DuoSeat.W.SetWindowPos(f.Handle, IntPtr.Zero, int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]), 4); };
        Application.Run(f);
    }
}
