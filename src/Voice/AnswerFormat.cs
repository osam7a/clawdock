using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Clawdock
{
    // One line of an answer, as the notch lays it out.
    class AnswerLine
    {
        public string Kind = "text";  // "text" | "heading" | "bullet" | "number" | "quote" | "code" | "rule"
        public string Marker = "";    // what goes in front of a list item
        public int Indent;            // how deep a list item is
        public bool Gap;              // a blank line came before it
        public string Code = "";      // a code block's text
        public List<TextPart> Parts = new List<TextPart>();
    }

    // The markdown Claude writes, for the notch to show as formatting. Answers arrive a few
    // characters at a time, so a mark that hasn't been closed yet is hidden rather than shown.
    static class AnswerFormat
    {
        const int DeepestIndent = 3;

        static readonly Regex Fence = new Regex(@"^\s*```", RegexOptions.Compiled);
        static readonly Regex Heading = new Regex(@"^\s*#{1,6}\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Bullet = new Regex(@"^(\s*)[-*+•]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Number = new Regex(@"^(\s*)(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Quote = new Regex(@"^\s*>\s?(.*)$", RegexOptions.Compiled);
        static readonly Regex Rule = new Regex(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);
        static readonly Regex TableRow = new Regex(@"^\s*\|", RegexOptions.Compiled);
        static readonly Regex TableLine = new Regex(@"^\s*\|[\s:\-|]+\|?\s*$", RegexOptions.Compiled);

        // Links come first, so that marks inside an address stay part of it.
        static readonly Regex Token = new Regex(
            @"`([^`\n]+)`|\[[^\]\n]*\]\([^)\s]*\)|https?://[^\s<>""`]+|\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?!\*)|(?<![A-Za-z0-9_])_(?=\S)(.+?)(?<=\S)_(?![A-Za-z0-9_])",
            RegexOptions.Compiled);

        public static List<AnswerLine> Parse(string text)
        {
            var lines = new List<AnswerLine>();
            StringBuilder code = null;
            AnswerLine block = null;
            bool gap = false, table = false;
            foreach (string line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                if (Fence.IsMatch(line))
                {
                    if (code != null) { code = null; block = null; continue; }
                    code = new StringBuilder();
                    block = new AnswerLine { Kind = "code", Gap = gap };
                    lines.Add(block);
                    gap = table = false;
                    continue;
                }
                if (code != null)
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(line);
                    block.Code = code.ToString();
                    continue;
                }
                if (line.Trim() == "") { gap = lines.Count > 0; table = false; continue; }
                if (TableRow.IsMatch(line))
                {
                    if (TableLine.IsMatch(line)) continue;
                    string row = string.Join("  |  ", Array.ConvertAll(line.Trim().Trim('|').Split('|'), c => c.Trim()));
                    if (table) lines[lines.Count - 1].Code += "\n" + row;
                    else lines.Add(new AnswerLine { Kind = "code", Gap = gap, Code = row });
                    table = true;
                    gap = false;
                    continue;
                }
                table = false;
                var parsed = ParseLine(line);
                parsed.Gap = gap;
                lines.Add(parsed);
                gap = false;
            }
            return lines;
        }

        static AnswerLine ParseLine(string line)
        {
            if (Rule.IsMatch(line)) return new AnswerLine { Kind = "rule" };
            var m = Heading.Match(line);
            if (m.Success) return new AnswerLine { Kind = "heading", Parts = Inline(m.Groups[1].Value) };
            m = Bullet.Match(line);
            if (m.Success) return new AnswerLine { Kind = "bullet", Marker = "•", Indent = IndentOf(m.Groups[1].Value), Parts = Inline(m.Groups[2].Value) };
            m = Number.Match(line);
            if (m.Success) return new AnswerLine { Kind = "number", Marker = m.Groups[2].Value + ".", Indent = IndentOf(m.Groups[1].Value), Parts = Inline(m.Groups[3].Value) };
            m = Quote.Match(line);
            if (m.Success) return new AnswerLine { Kind = "quote", Parts = Inline(m.Groups[1].Value) };
            return new AnswerLine { Parts = Inline(line) };
        }

        static int IndentOf(string space) { return Math.Min(DeepestIndent, space.Replace("\t", "  ").Length / 2); }

        internal static List<TextPart> Inline(string text)
        {
            var parts = new List<TextPart>();
            Scan(WithoutUnfinishedMarks(text), false, false, false, parts);
            return parts;
        }

        static string WithoutUnfinishedMarks(string s)
        {
            foreach (string mark in new[] { "**", "~~", "`" })
                if (Regex.Matches(s, Regex.Escape(mark)).Count % 2 == 1) s = s.Remove(s.LastIndexOf(mark, StringComparison.Ordinal), mark.Length);
            // The first half of a ** that's still arriving.
            if (s.EndsWith("*", StringComparison.Ordinal) && !s.EndsWith("**", StringComparison.Ordinal) && Regex.Matches(s, @"\*").Count % 2 == 1) s = s.Substring(0, s.Length - 1);
            return s;
        }

        static void Scan(string text, bool bold, bool italic, bool strike, List<TextPart> parts)
        {
            int at = 0;
            foreach (Match m in Token.Matches(text))
            {
                if (m.Index > at) parts.Add(new TextPart { Text = text.Substring(at, m.Index - at), Bold = bold, Italic = italic, Strike = strike });
                at = m.Index + m.Length;
                if (m.Groups[1].Success) parts.Add(new TextPart { Text = m.Groups[1].Value, Code = true, Bold = bold, Italic = italic, Strike = strike });
                else if (m.Groups[2].Success) Scan(m.Groups[2].Value, true, italic, strike, parts);
                else if (m.Groups[3].Success) Scan(m.Groups[3].Value, true, italic, strike, parts);
                else if (m.Groups[4].Success) Scan(m.Groups[4].Value, bold, italic, true, parts);
                else if (m.Groups[5].Success) Scan(m.Groups[5].Value, bold, true, strike, parts);
                else if (m.Groups[6].Success) Scan(m.Groups[6].Value, bold, true, strike, parts);
                else
                    foreach (var link in AnswerLinks.Split(m.Value)) // only web addresses become links
                    {
                        link.Bold = bold;
                        link.Italic = italic;
                        link.Strike = strike;
                        parts.Add(link);
                    }
            }
            if (at < text.Length) parts.Add(new TextPart { Text = text.Substring(at), Bold = bold, Italic = italic, Strike = strike });
        }
    }
}
