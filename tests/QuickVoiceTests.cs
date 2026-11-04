using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClaudeNotch.Tests
{
    static class QuickVoiceTests
    {
        // ---- what's shown and read aloud ------------------------------------

        public static void Card_and_tags_are_never_shown()
        {
            Check.Equal("Hi there", QuickVoice.WithoutCard("Hi there<card>{\"value\":\"1\"}</card>"));
            Check.Equal("Hi ", QuickVoice.WithoutCard("Hi <ask-session id=\"abc\">what now</ask-session>"));
            Check.Equal("Hi ", QuickVoice.WithoutCard("Hi <new-project name=\"x\" model=\"opus\">build it</new-project>"));
            Check.Equal("", QuickVoice.WithoutCard(null));
        }

        // The answer arrives a few characters at a time, and what was shown or spoken can't be taken back.
        public static void While_streaming_nothing_leaks_and_text_only_grows()
        {
            foreach (string answer in new[]
            {
                "Before <card>{\"value\":\"42\"}</card>after",
                "Before <ask-session id=\"abc\">secret question</ask-session>after",
                "Before <new-project name=\"x\" model=\"opus\" effort=\"high\">secret request</new-project>after",
                "3 < 4 and 5 > 2",
                "<CARD>{\"value\":\"42\"}</CARD>",
            })
            {
                string shown = "";
                for (int n = 0; n <= answer.Length; n++)
                {
                    string now = QuickVoice.WithoutCard(answer.Substring(0, n));
                    Check.That(now.StartsWith(shown, StringComparison.Ordinal), "shrank from [" + shown + "] to [" + now + "]");
                    foreach (string secret in new[] { "42", "secret", "{", "id=", "name=" }) Check.HasNot(now, secret);
                    shown = now;
                }
            }
        }

        public static void Display_removes_markdown_and_sources()
        {
            Check.Equal("Big news and a link", QuickVoice.Display("# Big **news** and [a link](https://x.y)\n\nSources: https://x.y"));
            Check.Equal("one\n\ntwo", QuickVoice.Display("one\n\n\n\n`two`"));
        }

        public static void Without_sources_cuts_the_list_at_the_end()
        {
            Check.Equal("Answer", QuickVoice.WithoutSources("Answer\n\n**Sources:**\n- a"));
            Check.Equal("Answer", QuickVoice.WithoutSources("Answer\nSource: a"));
            Check.Equal("The sources say so", QuickVoice.WithoutSources("The sources say so"));
        }

        public static void Speakable_reads_well_aloud()
        {
            Check.Equal("Title see the docs and done", QuickVoice.Speakable("# Title\nsee [the docs](https://a.b) https://c.d and\n```\ncode\n```\n**done**"));
            Check.Equal("one two", QuickVoice.Speakable("- one\n2. two"));
        }

        // ---- cards ----------------------------------------------------------

        static QuickCard Card(string json) { return QuickVoice.ParseCard("x<card>" + json.Replace('\'', '"') + "</card>"); }

        public static void No_card_or_a_broken_or_empty_one_is_nothing()
        {
            Check.That(QuickVoice.ParseCard("no card") == null, "no card");
            Check.That(QuickVoice.ParseCard(null) == null, "null");
            Check.That(Card("{broken") == null, "broken");
            Check.That(Card("{'title':'only a title'}") == null, "nothing to show");
        }

        public static void Card_kind_follows_its_contents()
        {
            Check.Equal("value", Card("{'value':'5'}").Kind);
            Check.Equal("list", Card("{'items':[['a','1']]}").Kind);
            Check.Equal("steps", Card("{'steps':['a']}").Kind);
            Check.Equal("compare", Card("{'columns':['a','b'],'rows':[['x','1','2']]}").Kind);
            Check.Equal("weather", Card("{'weather':'Sunny','value':'20'}").Kind);
            Check.Equal("sunny", Card("{'weather':'Sunny'}").Weather);
        }

        public static void Items_and_steps_come_in_either_shape_and_are_capped()
        {
            var card = Card("{'items':[['a','1'],{'name':'b','value':'2'},'c']}");
            Check.Equal(3, card.Items.Count);
            Check.Equal("2", card.Items[1].Value);
            Check.Equal("c", card.Items[2].Key);
            Check.Equal("x", Card("{'steps':[{'text':'x'}]}").Steps[0]);
            Check.Equal(8, Card("{'steps':['1','2','3','4','5','6','7','8','9','10']}").Steps.Count);
        }

        public static void Compare_rows_fit_the_columns()
        {
            var card = Card("{'columns':['a','b'],'rows':[['x','1'],['y','1','2','3'],['too short']]}");
            Check.Equal(2, card.Rows.Count);
            Check.Equal(3, card.Rows[0].Length);
            Check.Equal("", card.Rows[0][2]);
            Check.Equal(3, card.Rows[1].Length);
        }

        public static void Progress_reads_fractions_and_percentages()
        {
            foreach (var t in new[] { Tuple.Create("0.68", 0.68), Tuple.Create("68", 0.68), Tuple.Create("68%", 0.68), Tuple.Create("0", 0.0), Tuple.Create("150", 1.0), Tuple.Create("-3", -1.0), Tuple.Create("soon", -1.0), Tuple.Create("1%", 0.01), Tuple.Create("0.5%", 0.005), Tuple.Create("100%", 1.0) })
                Check.Near(t.Item2, Card("{'value':'x','progress':'" + t.Item1 + "'}").Progress, t.Item1);
            Check.Near(-1, Card("{'value':'x'}").Progress);
        }

        public static void Change_direction()
        {
            Check.That(Card("{'value':'x','change':'+3%'}").ChangeUp == true, "up");
            Check.That(Card("{'value':'x','change':'↓ 2'}").ChangeUp == false, "down");
            Check.That(Card("{'value':'x','change':'same'}").ChangeUp == null, "neither");
            Check.That(Card("{'value':'x'}").ChangeUp == null, "none");
        }

        // ---- what may run without asking ------------------------------------

        public static void Tools_that_only_look_things_up_are_safe()
        {
            foreach (string tool in new[] { "Read", "Grep", "Glob", "WebSearch", "WebFetch", "TodoWrite", "mcp__x__get_file", "mcp__x__list_items", "mcp__x__SEARCH" })
                Check.That(QuickVoice.SafeTool(tool), tool);
        }

        public static void Tools_that_act_must_ask()
        {
            foreach (string tool in new[] { "Bash", "Write", "Edit", "Agent", "PowerShell", "NotebookEdit", "", "mcp__x__send_message", "mcp__x__delete_file", "mcp__x__create_item", "mcp__get__delete", "mcp__list__update_thing", "readme" })
                Check.That(!QuickVoice.SafeTool(tool), tool);
        }

        // ---- command lines --------------------------------------------------

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CommandLineToArgvW(string line, out int count);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr p);

        // What Windows itself reads back from the command line.
        static string[] Split(string line)
        {
            int n;
            IntPtr p = CommandLineToArgvW("exe " + line, out n);
            var result = new string[n - 1];
            for (int i = 1; i < n; i++) result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size));
            LocalFree(p);
            return result;
        }

        public static void Arguments_arrive_exactly_as_given()
        {
            var args = new List<string>
            {
                "-p", "", "two words", @"C:\Program Files\x\", @"C:\no\spaces\", "he said \"hi\"", @"slash\""quote", "tab\there", "line\nbreak", "ünï ✓",
                "--tools", "", @"ends with two\\",
            };
            var back = Split(QuickVoice.JoinArgs(args));
            Check.Equal(args.Count, back.Length);
            for (int i = 0; i < back.Length; i++) Check.Equal(args[i], back[i], "argument " + i);
        }
    }
}
