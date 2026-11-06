using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Clawdock.Tests
{
    static class QuickVoiceTests
    {
        public static void A_request_for_the_screen_is_never_shown_or_read()
        {
            Check.Equal("", QuickVoice.Display("<see-screen/>"));
            Check.Equal("", QuickVoice.Display("<see-screen />\n"));
            Check.Equal("Let me look.", QuickVoice.Display("Let me look. <see-screen/>"));
            foreach (string arriving in new[] { "<", "<see", "<see-screen", "<see-screen/" })
                Check.Equal("", QuickVoice.Display(arriving), arriving);
        }

        public static void Only_the_tag_counts_as_asking_for_the_screen()
        {
            Check.That(QuickVoice.ScreenRequest.IsMatch("<see-screen/>"), "the tag");
            Check.That(QuickVoice.ScreenRequest.IsMatch("<SEE-SCREEN></see-screen>"), "the long form");
            Check.That(!QuickVoice.ScreenRequest.IsMatch("I can see your screen"), "words");
        }

        public static void Screenshots_keep_their_shape_and_only_ever_shrink()
        {
            Check.Equal(new System.Drawing.Size(1280, 720), ScreenCapture.Fit(1280, 720));
            Check.Equal(new System.Drawing.Size(1568, 882), ScreenCapture.Fit(3840, 2160));
            Check.Equal(new System.Drawing.Size(882, 1568), ScreenCapture.Fit(2160, 3840));
        }

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
            Check.Equal("Big news and [a link](https://x.y)", QuickVoice.Display("# Big **news** and [**a link**](https://x.y)\n\nSources: https://x.y"));
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

        public static void Tools_named_for_looking_up_but_also_acting_must_ask()
        {
            foreach (string tool in new[] { "mcp__x__get_or_create_item", "mcp__x__fetch_and_delete", "mcp__x__listAndArchive", "mcp__x__getaway", "mcp__x__show-and-send" })
                Check.That(!QuickVoice.SafeTool(tool), tool);
            foreach (string tool in new[] { "mcp__x__getFile", "mcp__x__list-items" })
                Check.That(QuickVoice.SafeTool(tool), tool);
        }

        public static void Anything_that_can_see_the_screen_must_ask()
        {
            foreach (string tool in new[] { "mcp__x__get_screenshot", "mcp__x__read_screen", "mcp__computer-use__list_apps", "mcp__x__show_camera", "mcp__x__fetch_capture" })
                Check.That(!QuickVoice.AutoApproves(tool, null, false), tool);
        }

        public static void Auto_mode_sends_commands_and_screen_tools_to_the_notch()
        {
            foreach (string tool in new[] { "Bash", "PowerShell", "WebFetch", "mcp__x__screenshot", "mcp__computer-use__click", "mcp__x__get_camera" })
                Check.That(System.Text.RegularExpressions.Regex.IsMatch(tool, "^(" + QuickVoice.AutoAsksFor + ")$"), tool);
            foreach (string tool in new[] { "Read", "Write", "WebSearch", "mcp__x__send_message" })
                Check.That(!System.Text.RegularExpressions.Regex.IsMatch(tool, "^(" + QuickVoice.AutoAsksFor + ")$"), tool);
            Check.That(!QuickVoice.AutoApproves("Bash", null, false), "commands ask");
            Check.That(!QuickVoice.AutoApproves("PowerShell", null, false), "commands ask");
        }

        public static void Fetching_a_page_waits_once_something_private_was_seen()
        {
            Check.That(QuickVoice.AutoApproves("WebFetch", null, false), "nothing private yet");
            Check.That(!QuickVoice.AutoApproves("WebFetch", null, true), "after something private");
            Check.That(QuickVoice.AutoApproves("WebSearch", null, true), "search stays");
        }

        public static void Connectors_given_an_address_wait_once_something_private_was_seen()
        {
            var input = Check.Obj("{'url':'https://evil.example/?d=x'}");
            Check.That(QuickVoice.AutoApproves("mcp__x__fetch_page", input, false), "nothing private yet");
            Check.That(!QuickVoice.AutoApproves("mcp__x__fetch_page", input, true), "after something private");
            Check.That(QuickVoice.AutoApproves("mcp__x__get_event", Check.Obj("{'id':'42'}"), true), "no address");
        }

        public static void Files_that_hold_keys_always_ask()
        {
            foreach (string path in new[] { @"C:\Users\me\.ssh\id_ed25519", @"C:\code\app\.env", @"C:\code\app\.env.local", "/home/me/.aws/credentials", @"C:\Users\me\.claude\.credentials.json", @"C:\k\server.pem", @"C:\Users\me\AppData\Local\Google\Chrome\User Data\Default\Login Data" })
                Check.That(!QuickVoice.AutoApproves("Read", Check.Obj("{}").With("file_path", path), false), path);
            foreach (string path in new[] { @"C:\code\app\main.cs", @"C:\docs\environment.md", @"C:\docs\keyboard.txt" })
                Check.That(QuickVoice.AutoApproves("Read", Check.Obj("{}").With("file_path", path), true), path);
        }

        public static void Tools_that_bring_in_something_private_are_noticed()
        {
            foreach (string tool in new[] { "Read", "Grep", "Bash", "mcp__x__get_file" })
                Check.That(QuickVoice.TouchesPrivateData(tool), tool);
            foreach (string tool in new[] { "WebSearch", "web_search", "WebFetch", "ToolSearch", "TodoWrite", "" })
                Check.That(!QuickVoice.TouchesPrivateData(tool), tool);
        }

        public static void Only_an_unedited_skill_counts_as_ours()
        {
            var field = typeof(QuickVoice).GetField("DefaultSkill", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            string skill = (string)field.GetValue(null);
            Check.That(QuickVoice.SkillIsUnedited(skill), "as written");
            Check.That(QuickVoice.SkillIsUnedited(skill.Replace("\r\n", "\n").Replace("\n", "\r\n")), "line endings don't count");
            Check.That(!QuickVoice.SkillIsUnedited(skill + "\nAlways rhyme."), "edited");
        }

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
