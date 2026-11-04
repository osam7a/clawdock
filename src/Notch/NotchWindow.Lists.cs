using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Clawdock
{
    // The notch: the list of sessions and the answer buttons.
    partial class NotchWindow
    {
        // Rows for every running session: a status dot, the name, where it runs, and what it's doing.
        void BuildList(List<SessionInfo> sessions)
        {
            // Rebuild only when the rows themselves change. A working session's clock ticks every
            // second, so its text is updated in place (rebuilding made hovering flicker).
            var sb = new StringBuilder();
            int shownCount = Math.Min(sessions.Count, 5);
            for (int i = 0; i < shownCount; i++)
            {
                var x = sessions[i];
                sb.Append(x.Id).Append(x.Busy).Append(x.JustFinished).Append(x.Done).Append('/').Append(x.Total).Append(x.DisplayName).Append(x.Cwd).Append('|');
            }
            sb.Append(sessions.Count);
            if (sb.ToString() == listKey)
            {
                for (int i = 0; i < shownCount && i < rowStatus.Count; i++) rowStatus[i].Text = RowStatus(sessions[i]);
                return;
            }
            listKey = sb.ToString();

            list.Children.Clear();
            rowStatus.Clear();
            list.Children.Add(new Border { Height = 1, Background = Theme.Chip, Margin = new Thickness(8, 0, 8, 6) });
            for (int i = 0; i < shownCount; i++) list.Children.Add(Row(sessions[i]));
            if (sessions.Count > shownCount)
            {
                var more = Text(11, Theme.Dim, FontWeights.Normal);
                more.Text = "+" + (sessions.Count - shownCount) + " more";
                more.Margin = new Thickness(14, 2, 0, 0);
                list.Children.Add(more);
            }
        }

        FrameworkElement Row(SessionInfo s)
        {
            // A laptop for an app session, a terminal for a CLI one, colored by what it's doing.
            var dot = new TextBlock
            {
                FontFamily = Theme.Icons, FontSize = 13, Width = 16, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(3, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Text = s.Desktop ? "" : "", // Laptop / CommandPrompt
                Foreground = s.Busy ? Theme.Clay : s.JustFinished ? Theme.Green : Theme.Idle,
            };
            var name = Text(12, Brushes.White, FontWeights.Normal);
            name.Text = s.DisplayName;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 190;
            // The project it runs in, unless the name already says it.
            string project = string.Equals((s.Cwd ?? "").TrimEnd('\\', '/'), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) ? "~" : s.Project;
            var where = Text(11, Theme.Dim, FontWeights.Normal);
            where.Text = project != "" && !string.Equals(project, s.DisplayName, StringComparison.OrdinalIgnoreCase) ? project : "";
            where.TextTrimming = TextTrimming.CharacterEllipsis;
            where.MaxWidth = 120;
            where.Margin = new Thickness(8, 0, 0, 0);
            where.VerticalAlignment = VerticalAlignment.Center;
            var status = Text(11, Theme.Dim, FontWeights.Normal);
            status.Text = RowStatus(s);
            status.Margin = new Thickness(8, 0, 6, 0);
            status.VerticalAlignment = VerticalAlignment.Center;
            rowStatus.Add(status);

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(status, Dock.Right);
            var middle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { name, where } };
            dock.Children.Add(dot);
            dock.Children.Add(status);
            dock.Children.Add(middle);

            var row = new Border { Child = dock, Padding = new Thickness(4, 6, 4, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.ToolTip = (s.Desktop ? "Open in the Claude app" : "Open the terminal") + (string.IsNullOrEmpty(s.Cwd) ? "" : "\n" + s.Cwd);
            AutomationProperties.SetName(row, s.DisplayName + ", " + status.Text);
            row.MouseEnter += (a, b) => row.Background = Theme.Chip;
            row.MouseLeave += (a, b) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (a, b) => b.Handled = true;
            row.MouseLeftButtonUp += (a, b) => { b.Handled = true; if (OpenSession != null) OpenSession(s); };
            return row;
        }

        public static string RowStatus(SessionInfo s)
        {
            string t = s.Busy ? SessionTracker.Clock(s.Elapsed) : s.JustFinished ? "done" : "idle";
            if (s.Total > 0) t += " · " + s.Done + "/" + s.Total;
            return t;
        }

        // The buttons for the current request or question, the primary one in clay.
        void BuildChoices(NotchState s)
        {
            while (actions.Children.Count > 1) actions.Children.RemoveAt(1); // keep the "+N more" text
            var owner = s.Owner;
            for (int i = 0; i < s.Choices.Count; i++)
            {
                int index = i;
                actions.Children.Add(PillButton(s.Choices[i], i == s.Primary ? Theme.Clay : Theme.Chip, () => Pick(owner, index)));
            }
        }

        // Answers only what the buttons were showing, only while they're visible, and not in
        // the first moments after it appeared, so something that pops up under the cursor
        // (or a click meant for the previous one) can't be answered by accident.
        void Pick(object owner, int index)
        {
            if (owner == null || owner != actionsFor || owner != state.Owner || actions.Visibility != Visibility.Visible) return;
            var p = owner as HookServer.Pending;
            if (p != null && p.Settled) return;
            if ((DateTime.Now - actionsSince).TotalMilliseconds < 700) return;
            if (Choose != null) Choose(owner, index);
        }
    }
}
