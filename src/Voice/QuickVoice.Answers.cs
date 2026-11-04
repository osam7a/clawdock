using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;

namespace Clawdock
{
    // Quick voice: turning an answer into what is shown, and its card.
    partial class QuickVoice
    {
        static readonly Regex SourcesStart = new Regex(@"(^|\n)[\s*#_]*Sources?\s*[:*]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // The answer without the sources list a web search adds at the end.
        public static string WithoutSources(string s)
        {
            var m = SourcesStart.Match(s ?? "");
            return m.Success ? s.Substring(0, m.Index).TrimEnd() : s;
        }

        static readonly Regex CardBlock = new Regex(@"<card>([\s\S]*?)</card>\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static readonly Regex ProjectBlock = new Regex(@"<new-project\b([^>]*)>([\s\S]*?)</new-project>\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex ProjectAttribute = new Regex(@"(\w+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
        static readonly Regex AskBlock = new Regex(@"<ask-session\s+id=""([^""]*)""\s*>([\s\S]*?)</ask-session>\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // The answer without its card. While a card is still arriving, it (and anything after it)
        // is held back, so its JSON is never shown or read aloud. Only ever grows as text arrives.
        internal static string WithoutCard(string s)
        {
            s = CardBlock.Replace(s ?? "", "");
            s = AskBlock.Replace(s, "");
            s = ProjectBlock.Replace(s, "");
            int ask = s.IndexOf("<ask-session", StringComparison.OrdinalIgnoreCase);
            if (ask >= 0) s = s.Substring(0, ask);
            int project = s.IndexOf("<new-project", StringComparison.OrdinalIgnoreCase);
            if (project >= 0) s = s.Substring(0, project);
            int open = s.IndexOf("<card", StringComparison.OrdinalIgnoreCase);
            if (open >= 0) s = s.Substring(0, open);
            int lt = s.LastIndexOf('<');
            if (lt >= 0 && ("<card>".StartsWith(s.Substring(lt), StringComparison.OrdinalIgnoreCase)
                || "<ask-session".StartsWith(s.Substring(lt), StringComparison.OrdinalIgnoreCase)
                || "<new-project".StartsWith(s.Substring(lt), StringComparison.OrdinalIgnoreCase))) s = s.Substring(0, lt);
            return s;
        }

        // The answer as the notch shows it: no card, no sources, no markdown marks. Web links stay,
        // as [label](address) or a bare address, for the notch to make clickable.
        public static string Display(string s)
        {
            s = WithoutSources(WithoutCard(s));
            s = Regex.Replace(s, @"\[([^\]\n]*)\]\([^)\n]*$", "$1"); // a link still arriving
            var kept = new List<string>();
            s = AnswerLinks.Token.Replace(s, m =>
            {
                string link, after = "";
                if (m.Groups[2].Success)
                {
                    string label = m.Groups[1].Value;
                    if (AnswerLinks.SafeUrl(m.Groups[2].Value) == null) return label;
                    link = "[" + label.Replace("**", "").Replace("__", "").Replace("`", "") + "](" + m.Groups[2].Value + ")";
                }
                else link = AnswerLinks.TrimUrl(m.Value, out after);
                kept.Add(link);
                return "" + (kept.Count - 1) + "" + after;
            });
            s = s.Replace("**", "").Replace("__", "").Replace("`", "");
            s = Regex.Replace(s, @"(?m)^\s*#+\s+", "");
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, "(\\d+)", m => kept[int.Parse(m.Groups[1].Value)]);
            return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        }

        public static QuickCard ParseCard(string s)
        {
            var m = CardBlock.Match(s ?? "");
            if (!m.Success) return null;
            Dictionary<string, object> d;
            try { d = Json.ParseObject(m.Groups[1].Value.Trim()); } catch { return null; }
            if (d == null) return null;
            var card = new QuickCard
            {
                Source = m.Groups[1].Value, Title = Json.Str(d, "title"), Value = Json.Str(d, "value"), Label = Json.Str(d, "label"),
                Note = Json.Str(d, "note"), Change = Json.Str(d, "change"), Weather = Json.Str(d, "weather").ToLowerInvariant(),
            };
            card.Progress = Fraction(Json.Str(d, "progress"));
            foreach (var item in List(d, "items"))
            {
                var pair = item as System.Collections.IList;
                var named = item as Dictionary<string, object>;
                string name = "", value = "";
                if (pair != null && pair.Count > 0) { name = Text(pair[0]); value = pair.Count > 1 ? Text(pair[1]) : ""; }
                else if (named != null) { name = Json.Str(named, "name"); value = Json.Str(named, "value"); }
                else name = Text(item);
                if (name != "" && card.Items.Count < 8) card.Items.Add(new KeyValuePair<string, string>(name, value));
            }
            foreach (var step in List(d, "steps"))
            {
                var named = step as Dictionary<string, object>;
                string text = named != null ? Json.Str(named, "text") : Text(step);
                if (text != "" && card.Steps.Count < 8) card.Steps.Add(text);
            }
            foreach (var col in List(d, "columns")) if (card.Columns.Count < 3) card.Columns.Add(Text(col));
            foreach (var row in List(d, "rows"))
            {
                var cells = row as System.Collections.IList;
                if (cells == null || cells.Count < 2 || card.Rows.Count >= 8) continue;
                var r = new string[card.Columns.Count + 1];
                for (int i = 0; i < r.Length; i++) r[i] = i < cells.Count ? Text(cells[i]) : "";
                card.Rows.Add(r);
            }
            foreach (var day in List(d, "days"))
            {
                var cells = day as System.Collections.IList;
                var named = day as Dictionary<string, object>;
                string[] r;
                if (named != null) r = new[] { Json.Str(named, "day"), Json.Str(named, "weather").ToLowerInvariant(), Json.Str(named, "high"), Json.Str(named, "low") };
                else if (cells != null && cells.Count > 0) r = new[] { Text(cells[0]), cells.Count > 1 ? Text(cells[1]).ToLowerInvariant() : "", cells.Count > 2 ? Text(cells[2]) : "", cells.Count > 3 ? Text(cells[3]) : "" };
                else continue;
                if (r[0] != "" && card.Days.Count < 5) card.Days.Add(r);
            }

            if (card.Steps.Count > 0) card.Kind = "steps";
            else if (card.Columns.Count >= 2 && card.Rows.Count > 0) card.Kind = "compare";
            else if (card.Weather != "" || card.Days.Count > 0) card.Kind = "weather";
            else if (card.Items.Count > 0) card.Kind = "list";
            else if (card.Value != "") card.Kind = "value";
            else return null;
            return card;
        }

        static System.Collections.IList List(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is System.Collections.IList ? (System.Collections.IList)v : new object[0];
        }

        static string Text(object v)
        {
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        // "0.68", "68" or "68%" as 0.68; -1 when there's none.
        static double Fraction(string s)
        {
            s = (s ?? "").Trim();
            bool percent = s.EndsWith("%");
            s = s.TrimEnd('%').Trim();
            double f;
            if (s == "" || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) || f < 0) return -1;
            if (percent || f > 1) f /= 100;
            return Math.Min(f, 1);
        }
    }
}
