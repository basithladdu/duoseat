// "Press this button" through Windows accessibility (UI Automation) instead of a real click.
// The taskbar, Start, Settings and Store apps ignore posted clicks, so they used to borrow Player A's
// real cursor for a moment (the twitch). Buttons, menu items, tabs and list items can be pressed this
// way with no cursor movement at all. Anything else (text boxes, drags, right-clicks) still borrows.
using System;
using System.Threading;
using System.Windows.Automation;

namespace DuoSeat
{
    static class SmartClick
    {
        static readonly ControlType[] Pressable = {
            ControlType.Button, ControlType.MenuItem, ControlType.ListItem, ControlType.TabItem, ControlType.Hyperlink,
            ControlType.SplitButton, ControlType.CheckBox, ControlType.RadioButton, ControlType.TreeItem
        };

        // true = pressed it, no real click needed
        public static bool TryPress(int x, int y, uint ownPid)
        {
            AutomationElement el = null;
            // finding the element can hang on a frozen app: give up quickly and let the real click happen
            Thread t = new Thread(delegate ()
            {
                try { el = AutomationElement.FromPoint(new System.Windows.Point(x, y)); } catch { el = null; }
            }) { IsBackground = true, Name = "uia" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            if (!t.Join(400) || el == null) return false;
            try
            {
                AutomationElement.AutomationElementInformation info = el.Current;
                if (info.ProcessId == ownPid || !info.IsEnabled) return false;
                if (Array.IndexOf(Pressable, info.ControlType) < 0) return false;
                object p;
                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out p)) { ((InvokePattern)p).Invoke(); return true; }
                if (el.TryGetCurrentPattern(TogglePattern.Pattern, out p)) { ((TogglePattern)p).Toggle(); return true; }
                if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) { ((SelectionItemPattern)p).Select(); return true; }
                if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p))
                {
                    ExpandCollapsePattern ec = (ExpandCollapsePattern)p;
                    if (ec.Current.ExpandCollapseState == ExpandCollapseState.Expanded) ec.Collapse(); else ec.Expand();
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
