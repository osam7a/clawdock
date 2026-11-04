using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeNotch
{
    // A piece of an answer: plain text, or text that links somewhere.
    class TextPart
    {
        public string Text, Url;
    }

    // Finding the links in an answer, and opening them. Answers can repeat what a web page says, so
    // only http and https links ever open, and the notch shows where a link goes before you click.
    static class AnswerLinks
    {
        // [label](address), or a bare web address.
        internal static readonly Regex Token = new Regex(@"\[([^\]\n]*)\]\(([^)\s]*)\)|https?://[^\s<>""`]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Replaced by tests, so that they don't open a browser.
        internal static Action<string> Opener = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        // The address if it's a web address, or null.
        public static string SafeUrl(string s)
        {
            Uri u;
            if (string.IsNullOrWhiteSpace(s) || !Uri.TryCreate(s.Trim(), UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            return u.Host == "" ? null : u.AbsoluteUri;
        }

        // Splits a bare address from what the sentence put right after it: "see x.com/a." or "(x.com/a)".
        public static string TrimUrl(string url, out string tail)
        {
            int end = url.Length;
            while (end > 0)
            {
                char c = url[end - 1];
                bool extra = ".,;:!?*_'".IndexOf(c) >= 0 || (c == ')' && Count(url, '(', end) < Count(url, ')', end)) || (c == ']' && Count(url, '[', end) < Count(url, ']', end));
                if (!extra) break;
                end--;
            }
            tail = url.Substring(end);
            return url.Substring(0, end);
        }

        static int Count(string s, char c, int end)
        {
            int n = 0;
            for (int i = 0; i < end; i++) if (s[i] == c) n++;
            return n;
        }

        public static List<TextPart> Split(string text)
        {
            var parts = new List<TextPart>();
            text = text ?? "";
            int at = 0;
            foreach (Match m in Token.Matches(text))
            {
                if (m.Index > at) parts.Add(new TextPart { Text = text.Substring(at, m.Index - at) });
                at = m.Index + m.Length;
                if (m.Groups[2].Success)
                {
                    string url = SafeUrl(m.Groups[2].Value);
                    string label = m.Groups[1].Value;
                    if (url == null) { if (label != "") parts.Add(new TextPart { Text = label }); }
                    else parts.Add(new TextPart { Text = label != "" ? label : url, Url = url });
                    continue;
                }
                string tail;
                string bare = TrimUrl(m.Value, out tail);
                string safe = SafeUrl(bare);
                if (safe == null) tail = m.Value;
                else parts.Add(new TextPart { Text = bare, Url = safe });
                if (tail != "") parts.Add(new TextPart { Text = tail });
            }
            if (at < text.Length) parts.Add(new TextPart { Text = text.Substring(at) });
            return parts;
        }

        public static void Open(string url)
        {
            string safe = SafeUrl(url);
            if (safe == null) return;
            try { Opener(safe); }
            catch (Exception e) { Log.Write("Couldn't open a link: " + e.Message); }
        }
    }
}
