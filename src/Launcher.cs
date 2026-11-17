// DuoSeat.exe - one file for both laptops. Asks "Player A or Player B?" on start.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace DuoSeat
{
    static class Launcher
    {
        [STAThread]
        static void Main(string[] args)
        {
            W.MakeDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string role = null;
            foreach (string a in args)
            {
                if (a == "--host" || a == "--pin") role = "A";
                if (a == "--client") role = "B";
            }
            if (role == null)
            {
                using (ChooseForm f = new ChooseForm())
                {
                    if (f.ShowDialog() != DialogResult.OK) return;
                    role = f.Role;
                }
            }
            if (role == "A") HostProgram.Run(); else ClientProgram.Run();
        }
    }

    class ChooseForm : Form
    {
        public string Role;

        public ChooseForm()
        {
            Text = "DuoSeat";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(500, 200);

            Label q = new Label { Text = "Which laptop is this?", Font = new Font("Segoe UI", 9.5f), Bounds = new Rectangle(20, 12, 460, 22) };
            Button a = Aero.CommandLink("I'm Player A",
                "This laptop has the files and the extra monitor or TV. Player B will work on it too.",
                new Rectangle(14, 40, 472, 74));
            a.Click += delegate { Role = "A"; DialogResult = DialogResult.OK; };
            Button b = Aero.CommandLink("I'm Player B",
                "Use this laptop's mouse and keyboard on Player A's extra screen, with my own cursor.",
                new Rectangle(14, 118, 472, 74));
            b.Click += delegate { Role = "B"; DialogResult = DialogResult.OK; };

            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { q, a, b });
            Aero.Dress(this, "DuoSeat", "Two people, one PC. Each gets their own cursor and keyboard.", 46);
            Control foot = Controls["footer"];
            cancel.Bounds = new Rectangle(foot.Width - 98, 11, 84, 25);
            foot.Controls.Add(cancel);
            Aero.Skin(cancel);
            CancelButton = cancel;
        }
    }
}
