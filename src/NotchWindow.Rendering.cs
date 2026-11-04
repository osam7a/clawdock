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

namespace ClaudeNotch
{
    // The notch: what it looks like for the current state.
    partial class NotchWindow
    {
        // ---- state ---------------------------------------------------------

        public void Render(NotchState s)
        {
            state = s;
            bool bars = s.Kind == "voice" || (s.Kind == "quick" && (s.Listening || s.Speaking));
            if (bars && !voiceTimer.IsEnabled) voiceTimer.Start();
            if (!bars) voiceTimer.Stop();
            Refresh();
        }

        void Refresh()
        {
            var s = state;
            bool alert = s.Kind == "alert";
            bool asking = alert || s.Kind == "question";
            bool quick = s.Kind == "quick";
            bool listening = quick && s.Listening;
            bool hoverExpand = hover && settings.ExpandOnHover;
            // The bar and dot open into the card by themselves when something needs you: a permission
            // request, a question, or quick voice.
            bool expanded = !Mini || hoverExpand || asking || quick;
            bool showList = hoverExpand && s.Sessions.Count > 0 && !asking && !quick;
            bool showActions = s.Owner != null && s.Choices.Count > 0 && expanded;
            if (!showActions) actionsFor = null;
            else if (actionsFor != s.Owner) { actionsFor = s.Owner; actionsSince = DateTime.Now; BuildChoices(s); }

            if (s.Pending != detailsFor) { detailsFor = s.Pending; detailsOpen = false; detailScroll.ScrollToTop(); } // never the last request's
            bool showDetails = CanOpenDetails && detailsOpen;
            detailText.Text = showDetails ? s.Pending.FullDetail : "";

            mascot.Mood = alert ? MascotMood.Alert : s.Kind == "question" ? MascotMood.Question : s.Mood;

            bool isOpen = asking || (quick && !s.QuickMinimized) || showList || showActions;
            double size = SizeOf(isOpen ? settings.NotchSizeOpen : settings.NotchSizeCollapsed);
            if (size != sizeNow)
            {
                sizeNow = size;
                var grow = new DoubleAnimation(size, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                sizing.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                sizing.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }

            title.Inlines.Clear();
            title.Inlines.Add(new Run(s.Title));
            bool showSub = (s.Kind == "session" || asking) && !string.IsNullOrEmpty(s.Detail);
            if (Compact && s.Kind == "session")
            {
                string brief = !string.IsNullOrEmpty(s.Short) ? s.Short : s.Detail;
                if (s.Total > 0 && brief != null) { int dot = brief.IndexOf(" · "); if (dot > 0) brief = brief.Substring(0, dot); }
                if (!string.IsNullOrEmpty(brief)) title.Inlines.Add(new Run("  " + brief) { Foreground = Theme.Dim, FontWeight = FontWeights.Normal });
                showSub = false;
            }
            subtitle.Text = s.Detail;
            // A question can be a full sentence: let it wrap onto a second line.
            subtitle.TextWrapping = s.Kind == "question" ? TextWrapping.Wrap : TextWrapping.NoWrap;
            subtitle.MaxHeight = s.Kind == "question" ? 34 : double.PositiveInfinity;
            if (s.Kind == "voice") title.Inlines.Clear();
            if (s.Kind == "voice") title.Inlines.Add(new Run(s.Muted ? "Muted" : "Listening"));
            // Minimized, the bar shows the answer's key figure (a price, a time) next to what it's doing.
            bool minimized = quick && s.QuickMinimized;
            var barCard = minimized && !listening && s.QuickNow != null ? s.QuickNow.Card : null;
            if (barCard != null)
            {
                title.Inlines.Clear();
                if ((barCard.Kind == "value" || barCard.Kind == "weather") && barCard.Value != "")
                {
                    KeyValuePair<string, Brush> sign;
                    if (barCard.Kind == "weather" && WeatherSigns.TryGetValue(barCard.Weather, out sign))
                        title.Inlines.Add(new Run(sign.Key + " ") { FontFamily = Theme.Symbols, Foreground = sign.Value, FontSize = Compact ? 13 : 15 });
                    title.Inlines.Add(new Run(barCard.Value) { FontSize = Compact ? 14 : 16, FontWeight = FontWeights.Bold });
                    if (barCard.Change != "") { title.Inlines.Add(new Run("  ")); title.Inlines.Add(ChangeRun(barCard, 12)); }
                    if (barCard.Label != "") title.Inlines.Add(new Run("  " + barCard.Label) { Foreground = Theme.Dim, FontWeight = FontWeights.Normal, FontSize = 12 });
                }
                else if (barCard.Title != "") title.Inlines.Add(new Run(barCard.Title));
                else if (barCard.Kind == "list") title.Inlines.Add(new Run(barCard.Items[0].Key + "  " + barCard.Items[0].Value));
                else if (barCard.Kind == "steps") title.Inlines.Add(new Run(barCard.Steps.Count + " steps"));
                else if (barCard.Kind == "compare") title.Inlines.Add(new Run(string.Join(" vs ", barCard.Columns)));
                else title.Inlines.Add(new Run(barCard.Label));
            }
            bool speakingBars = quick && s.Speaking && !listening;
            foreach (var b in bars) b.Fill = speakingBars ? Theme.Clay : quick ? Theme.Green : Theme.Clay;
            minGlyph.Text = minimized ? "" : ""; // FullScreen / ChromeMinimize
            minButton.ToolTip = minimized ? "Show the whole conversation" : "Minimize to a small bar (keeps listening for the shortcut)";
            AutomationProperties.SetName(minButton, minimized ? "Expand" : "Minimize");

            bool hasTasks = s.Kind == "session" && s.Total > 0 && !Compact;
            bool compactHasTasks = s.Kind == "session" && s.Total > 0 && Compact;
            if (compactHasTasks)
            {
                miniDone.Width = new GridLength(s.Done, GridUnitType.Star);
                miniLeft.Width = new GridLength(Math.Max(s.Total - s.Done, 0), GridUnitType.Star);
                compactCount.Text = s.Done + "/" + s.Total;
            }
            if (hasTasks)
            {
                progressDone.Width = new GridLength(s.Done, GridUnitType.Star);
                progressLeft.Width = new GridLength(Math.Max(s.Total - s.Done, 0), GridUnitType.Star);
            }
            micGlyph.Text = s.Muted ? "" : ""; // MicOff / Microphone
            micButton.Background = s.Muted ? Theme.Clay : Theme.Chip;
            closeButton.ToolTip = s.Kind == "voice" ? "End voice and hide" : quick ? "Stop and hide" : "Hide for now";
            endVoiceItem.Visibility = s.Kind == "voice" ? Visibility.Visible : Visibility.Collapsed;
            UpdateActionsText();
            if (s.Kind == "voice" || listening || speakingBars) TickVoice();
            if (showList) BuildList(s.Sessions);
            if (quick && !minimized) UpdateQuick(s);
            UpdateMini(s);

            string key = s.Kind + "|" + listening + "|" + minimized + "|" + speakingBars + "|" + (barCard != null) + "|" + hover + "|" + s.Visible + "|" + hasTasks + "|" + compactHasTasks + "|" + showList + "|" + showActions + "|" + showSub + "|" + showDetails + "|" + settings.Style + "|" + settings.Edge;
            if (key == layoutKey) return;
            layoutKey = key;

            card.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            mini.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            subtitle.Visibility = showSub ? Visibility.Visible : Visibility.Collapsed;
            voicePanel.Visibility = s.Kind == "voice" || listening || speakingBars ? Visibility.Visible : Visibility.Collapsed;
            timerText.Visibility = s.Kind == "voice" || (listening && !minimized) ? Visibility.Visible : Visibility.Collapsed;
            openChatButton.Visibility = quick && hover && !minimized ? Visibility.Visible : Visibility.Collapsed;
            inputBar.Visibility = quick && s.QuickTyping ? Visibility.Visible : Visibility.Collapsed;
            SetTyping(quick && s.QuickTyping);
            minButton.Visibility = quick && hover ? Visibility.Visible : Visibility.Collapsed;
            clearButton.Visibility = quick && hover && !minimized && s.QuickCanClear ? Visibility.Visible : Visibility.Collapsed;
            micButton.Visibility = quick ? Visibility.Collapsed : Visibility.Visible;
            alertDot.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
            detailScroll.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
            detailScroll.MaxHeight = Math.Min(Compact ? 240 : 320, WorkArea().Height / SizeOf(settings.NotchSizeOpen) - 200);
            quickScroll.Visibility = quick && !minimized ? Visibility.Visible : Visibility.Collapsed;
            quickScroll.MaxHeight = Math.Min(Compact ? 380 : 460, WorkArea().Height / SizeOf(settings.NotchSizeOpen) - 200); // large sizes still fit the screen
            alertDot.Fill = alert ? Theme.Amber : Theme.Blue;
            closeButton.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
            progressTrack.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
            compactTasks.Visibility = compactHasTasks ? Visibility.Visible : Visibility.Collapsed;
            actions.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
            list.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;

            // A notice (a permission request to answer elsewhere) is a small card with one button.
            bool notice = alert && s.Pending == null;
            double w = (notice ? (Compact ? 270 : 300) : CardWidth(s.Kind)) + (hover && !Mini && !quick ? 26 : 0); // quick: don't reflow what you're reading
            if (compactHasTasks) w += 56;
            if (showDetails) w = Math.Max(w, Compact ? 420 : 460);
            if (minimized) w = (Compact ? 290 : 320) + (barCard != null ? 90 : 0);
            if (showList || (showActions && !notice)) w = Math.Max(w, Compact ? 330 : 360);
            Animate(card, WidthProperty, w, 220);
            if (s.Kind == "question" || quick) header.Height = double.NaN; // the text may wrap: fit it
            else header.Height = Compact ? 30 : s.Kind == "session" || alert ? (hasTasks ? 54 : 56) : 38;
            header.Margin = s.Kind == "question" || quick ? (Compact || minimized ? new Thickness(0, 7, 0, 7) : new Thickness(0, 10, 0, 6)) : new Thickness(0);
            double r = Compact ? 14 : 18;
            switch (settings.Edge)
            {
                case "bottom": card.CornerRadius = new CornerRadius(r, r, 0, 0); break;
                case "left": card.CornerRadius = new CornerRadius(0, r, r, 0); break;
                case "right": card.CornerRadius = new CornerRadius(r, 0, 0, r); break;
                default: card.CornerRadius = new CornerRadius(0, 0, r, r); break;
            }

            if (s.Visible != shown)
            {
                shown = s.Visible;
                if (shown) Show();
                double away = 90;
                bool horizontalSlide = Vertical;
                double sign = settings.Edge == "top" || settings.Edge == "left" ? -1 : 1;
                var prop = horizontalSlide ? TranslateTransform.XProperty : TranslateTransform.YProperty;
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                slide.BeginAnimation(TranslateTransform.YProperty, null);
                slide.X = 0; slide.Y = 0;
                if (shown) { if (horizontalSlide) slide.X = sign * away; else slide.Y = sign * away; }

                var move = new DoubleAnimation(shown ? 0 : sign * away, TimeSpan.FromMilliseconds(shown ? 320 : 200))
                {
                    EasingFunction = shown
                        ? (IEasingFunction)new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
                        : new CubicEase { EasingMode = EasingMode.EaseIn },
                };
                var fade = new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(shown ? 200 : 180));
                fade.Completed += (s2, e) => { if (!shown) { Hide(); hover = false; } };
                slide.BeginAnimation(prop, move);
                holder.BeginAnimation(OpacityProperty, fade);
            }
        }

        bool CanOpenDetails { get { return state.Kind == "alert" && state.Pending != null && !string.IsNullOrEmpty(state.Pending.FullDetail); } }

        // Clicking opening or closing the details moves the buttons, so they wait again: the
        // second click of a double click mustn't land on Allow.
        void ToggleDetails()
        {
            detailsOpen = !detailsOpen;
            actionsSince = DateTime.Now;
            detailScroll.ScrollToTop();
            Refresh();
        }

        // Beside the buttons: how to see the details, and how many more requests wait.
        void UpdateActionsText()
        {
            var parts = new List<string>();
            if (CanOpenDetails)
            {
                if (detailsOpen && detailScroll.ScrollableHeight - detailScroll.VerticalOffset > 1) parts.Add("Scroll for more");
                parts.Add(detailsOpen ? "Hide details" : "Show details");
            }
            if (state.MorePending > 0) parts.Add("+" + state.MorePending + " more");
            moreText.Text = string.Join(" · ", parts);
        }

        double CardWidth(string kind)
        {
            if (Compact) return kind == "quick" ? 420 : kind == "alert" || kind == "question" ? 330 : kind == "session" ? 280 : kind == "voice" ? 230 : 110;
            return kind == "quick" ? 460 : kind == "question" ? 380 : kind == "alert" ? 360 : kind == "session" ? 340 : kind == "voice" ? 280 : 150;
        }


        void TickVoice()
        {
            var t = DateTime.Now - state.VoiceStarted;
            timerText.Text = string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            // Quick voice follows how loud you (or the answer) really are, when that's known.
            double level = state.Kind == "quick" && QuickLevel != null ? QuickLevel() : -1;
            for (int i = 0; i < bars.Length; i++)
            {
                double center = 1 - Math.Abs(i - 2) / 3.0; // middle bars run taller
                if (level >= 0) bars[i].Height = 3 + Math.Min(1, level * 1.6) * 15 * (0.45 + 0.55 * center) * (0.7 + rnd.NextDouble() * 0.6);
                else bars[i].Height = state.Muted ? 3 : 4 + rnd.NextDouble() * 14 * center + rnd.NextDouble() * 3;
                bars[i].Opacity = state.Muted ? 0.4 : 1;
            }
        }
    }
}
