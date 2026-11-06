using System.Collections.Generic;
using System.Linq;

namespace Clawdock.Tests
{
    static class AnswerFormatTests
    {
        static TextPart Only(string text)
        {
            var parts = AnswerFormat.Inline(text);
            Check.Equal(1, parts.Count, text);
            return parts[0];
        }

        static string Plain(List<TextPart> parts) { return string.Concat(parts.Select(p => p.Text)); }

        public static void Bold_italic_strike_and_code_lose_their_marks()
        {
            var bold = Only("**big**");
            Check.Equal("big", bold.Text);
            Check.That(bold.Bold, "bold");
            Check.That(Only("__big__").Bold, "bold with underscores");
            Check.That(Only("*slanted*").Italic, "italic");
            Check.That(Only("_slanted_").Italic, "italic with underscores");
            Check.That(Only("~~gone~~").Strike, "strike");
            Check.That(Only("`x = 1`").Code, "code");
        }

        public static void Marks_can_be_nested()
        {
            var part = Only("**_both_**");
            Check.That(part.Bold && part.Italic, "bold and italic");
        }

        public static void Text_around_marks_stays_plain()
        {
            var parts = AnswerFormat.Inline("a **b** c");
            Check.Equal(3, parts.Count);
            Check.That(!parts[0].Bold && parts[1].Bold && !parts[2].Bold, "only the middle is bold");
            Check.Equal("a b c", Plain(parts));
        }

        public static void Ordinary_symbols_arent_marks()
        {
            foreach (string text in new[] { "2 * 3 * 4", "snake_case_name", "a*b*c" })
            {
                var parts = AnswerFormat.Inline(text);
                Check.Equal(text, Plain(parts));
                Check.That(parts.All(p => !p.Italic && !p.Bold), text);
            }
        }

        public static void Links_stay_whole_and_only_web_addresses_link()
        {
            var part = Only("https://x.io/b__c__d");
            Check.Equal("https://x.io/b__c__d", part.Url);
            Check.Equal("https://a.b/", Only("**[docs](https://a.b)**").Url);
            Check.That(Only("**[docs](https://a.b)**").Bold, "a bold link");
            var unsafeLink = Only("[run this](file:///C:/x.exe)");
            Check.Equal("run this", unsafeLink.Text);
            Check.Equal(null, unsafeLink.Url);
        }

        public static void A_mark_still_arriving_is_never_shown()
        {
            foreach (string text in new[] { "**bol", "*", "**", "some `cod", "~~gon" })
                Check.HasNot(Plain(AnswerFormat.Inline(text)), text.StartsWith("some") ? "`" : text.Substring(0, 1));
        }

        public static void Lists_keep_their_markers_and_depth()
        {
            var lines = AnswerFormat.Parse("- one\n  - inner\n* two\n3. three\n4) four");
            Check.Equal("bullet", lines[0].Kind);
            Check.Equal("•", lines[0].Marker);
            Check.Equal(0, lines[0].Indent);
            Check.Equal(1, lines[1].Indent);
            Check.Equal("bullet", lines[2].Kind);
            Check.Equal("number", lines[3].Kind);
            Check.Equal("3.", lines[3].Marker);
            Check.Equal("4.", lines[4].Marker);
            Check.Equal("one", Plain(lines[0].Parts));
        }

        public static void Headings_quotes_rules_and_gaps()
        {
            var lines = AnswerFormat.Parse("# Title\n\n> said\n\n---\nnext");
            Check.Equal("heading", lines[0].Kind);
            Check.Equal("Title", Plain(lines[0].Parts));
            Check.Equal("quote", lines[1].Kind);
            Check.That(lines[1].Gap, "a blank line before it");
            Check.Equal("rule", lines[2].Kind);
            Check.Equal("text", lines[3].Kind);
            Check.That(!lines[3].Gap, "no blank line before it");
        }

        public static void Code_blocks_keep_their_text_even_while_arriving()
        {
            var done = AnswerFormat.Parse("```cs\nvar **x** = 1;\n  y();\n```\nafter");
            Check.Equal("code", done[0].Kind);
            Check.Equal("var **x** = 1;\n  y();", done[0].Code);
            Check.Equal("after", Plain(done[1].Parts));
            Check.Equal("var x", AnswerFormat.Parse("```\nvar x")[0].Code);
        }

        public static void Tables_show_as_aligned_rows()
        {
            var lines = AnswerFormat.Parse("| a | b |\n|---|---|\n| 1 | 2 |");
            Check.Equal(1, lines.Count);
            Check.Equal("code", lines[0].Kind);
            Check.Equal("a  |  b\n1  |  2", lines[0].Code);
        }

        public static void Marks_are_never_read_aloud()
        {
            Check.Equal("big slanted word gone x", QuickVoice.Speakable("**big** *slanted* _word_ ~~gone~~ `x`"));
            Check.Equal("said", QuickVoice.Speakable("> said"));
            Check.Equal("before after", QuickVoice.Speakable("before\n---\nafter"));
            Check.Equal("before after", QuickVoice.Speakable("before\n| a | b |\n|---|---|\nafter"));
            Check.Equal("snake_case_name", QuickVoice.Speakable("snake_case_name"));
        }

        public static void The_screen_keeps_formatting_but_not_the_cards_or_sources()
        {
            Check.Equal("**big**\n- one", QuickVoice.ForScreen("<card>{\"value\":\"1\"}</card>**big**\n- one\n\nSources: https://x.y"));
            Check.Equal("See the docs", QuickVoice.ForScreen("See [the docs](https://x.i"));
        }
    }
}
