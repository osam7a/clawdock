using System.Collections.Generic;

namespace Clawdock.Tests
{
    static class ControllerTests
    {
        public static void The_x_on_a_request_or_question_closes_only_that()
        {
            Check.That(Controller.XClosesOnlyThat("alert", false, false), "a permission request");
            Check.That(Controller.XClosesOnlyThat("question", false, false), "a question");
            Check.That(Controller.XClosesOnlyThat("alert", true, false), "a session's request over quick voice");
            Check.That(!Controller.XClosesOnlyThat("alert", true, true), "quick voice's own request stops it");
            Check.That(!Controller.XClosesOnlyThat("session", false, false), "a session's card hides the notch");
            Check.That(!Controller.XClosesOnlyThat("quick", true, false), "quick voice stops");
        }

        public static void A_maximized_window_isnt_fullscreen_even_with_the_taskbar_hidden()
        {
            Check.That(!ClaudeWatcher.CoversMonitor(-8, -8, 1928, 1088, 0, 0, 1920, 1080, true), "maximized, hanging over the edges");
            Check.That(ClaudeWatcher.CoversMonitor(0, 0, 1920, 1080, 0, 0, 1920, 1080, true), "maximized, but fits exactly");
            Check.That(ClaudeWatcher.CoversMonitor(0, 0, 1920, 1080, 0, 0, 1920, 1080, false), "fullscreen");
            Check.That(ClaudeWatcher.CoversMonitor(-1, -1, 1921, 1081, 0, 0, 1920, 1080, false), "a game a little past the edges");
            Check.That(!ClaudeWatcher.CoversMonitor(100, 100, 900, 700, 0, 0, 1920, 1080, false), "an ordinary window");
        }

        // ---- the one-line summary on the card -------------------------------

        public static void Summary_prefers_the_command_then_file_then_url()
        {
            Check.Equal("Bash: dir", Controller.DescribeTool("Bash", Check.Obj("{'command':'dir','file_path':'a','description':'b'}")));
            Check.Equal("Edit: a.txt", Controller.DescribeTool("Edit", Check.Obj("{'file_path':'a.txt','description':'b'}")));
            Check.Equal("WebFetch: https://x", Controller.DescribeTool("WebFetch", Check.Obj("{'url':'https://x'}")));
            Check.Equal("Agent: look around", Controller.DescribeTool("Agent", Check.Obj("{'description':'look around'}")));
        }

        public static void Summary_with_little_to_say()
        {
            Check.Equal("Bash", Controller.DescribeTool("Bash", null));
            Check.Equal("", Controller.DescribeTool("", null));
            Check.Equal("dir", Controller.DescribeTool("", Check.Obj("{'command':'dir'}")));
        }

        public static void Summary_stays_on_one_short_line()
        {
            string line = Controller.DescribeTool("Bash", new Dictionary<string, object> { { "command", "a\r\nb" + new string('x', 500) } });
            Check.HasNot(line, "\n");
            Check.HasNot(line, "\r");
            Check.That(line.Length < 220, "length " + line.Length);
            Check.That(line.EndsWith("…"), "marks the cut");
        }

        // ---- everything, when you expand the request ------------------------

        public static void Details_show_every_input_with_a_friendly_label()
        {
            string text = Controller.FullRequest(Check.Obj("{'command':'dir','file_path':'a.txt','custom_thing':'x'}"), @"C:\work");
            Check.Has(text, "Command: dir");
            Check.Has(text, "File: a.txt");
            Check.Has(text, "custom_thing: x");
            Check.That(text.EndsWith(@"Folder: C:\work"), text);
        }

        public static void Details_never_lose_a_multi_line_value()
        {
            string text = Controller.FullRequest(new Dictionary<string, object> { { "command", "one\r\ntwo" } }, "");
            Check.Equal("Command:\none\ntwo", text);
        }

        public static void Details_show_values_that_arent_text()
        {
            string text = Controller.FullRequest(Check.Obj("{'replace_all':true,'limit':5,'nothing':null,'list':['a'],'more':{'k':'v'}}"), "");
            Check.Has(text, "\"a\"");
            Check.Has(text, "\"k\": \"v\"");
            Check.Has(text, "limit: 5\n");
            Check.Has(text.ToLowerInvariant(), "replace_all: true\n");
            Check.Has(text, "nothing: \n");
        }

        public static void Details_say_how_much_was_cut()
        {
            string text = Controller.FullRequest(new Dictionary<string, object> { { "command", new string('x', 25000) } }, "");
            Check.That(text.Length < 20100, "length " + text.Length);
            Check.Has(text, "more characters");
        }

        public static void Details_with_nothing_given()
        {
            Check.Equal("", Controller.FullRequest(null, ""));
            Check.Equal("Folder: C:\\x", Controller.FullRequest(null, "C:\\x"));
        }
    }
}
