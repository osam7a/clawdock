using System.IO;
using System.Linq;

namespace ClaudeNotch.Tests
{
    static class SessionBriefTests
    {
        // Transcript lines, written with single quotes.
        static SessionBrief.Digest Read(int events, params string[] lines)
        {
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(file, lines.Select(l => l.Replace('\'', '"')));
                return SessionBrief.ReadWhole(file, events);
            }
            finally { File.Delete(file); }
        }

        static string User(string text) { return "{'type':'user','cwd':'C:/work/repo','timestamp':'2026-01-01T10:00:00Z','message':{'content':'" + text + "'}}"; }
        static string Assistant(string blocks) { return "{'type':'assistant','message':{'model':'claude-x','content':[" + blocks + "]}}"; }
        static string Said(string text) { return Assistant("{'type':'text','text':'" + text + "'}"); }
        static string Tool(string id, string name, string input) { return Assistant("{'type':'tool_use','id':'" + id + "','name':'" + name + "','input':" + input + "}"); }
        static string Result(string id) { return "{'type':'user','message':{'content':[{'type':'tool_result','tool_use_id':'" + id + "'}]}}"; }

        public static void Reads_the_conversation()
        {
            var d = Read(10, User("first question"), Said("first answer"), User("second question"), Said("second answer"));
            Check.Equal("first question", d.FirstAsked);
            Check.Equal("second question", d.Asked);
            Check.Equal("second answer", d.Said);
            Check.Equal("claude-x", d.Model);
            Check.Equal("C:/work/repo", d.Cwd);
            Check.Equal(4, d.Events.Count);
            Check.Equal("You", d.Events[0].Who);
            Check.Equal("Claude", d.Events[1].Who);
            Check.Equal("thinking", d.Now);
        }

        public static void Skips_what_the_user_never_said()
        {
            var d = Read(10,
                User("real question"),
                User("<system-reminder>injected</system-reminder>"),
                User("This session is being continued from a previous conversation that ran out of context."),
                "{'type':'user','isMeta':true,'message':{'content':'a meta message'}}",
                "{'type':'user','isSidechain':true,'message':{'content':'a subagent prompt'}}");
            Check.Equal("real question", d.Asked);
            Check.Equal(1, d.Events.Count);
        }

        public static void Removes_injected_text_around_a_real_question()
        {
            Check.Equal("the question", Read(10, User("<system-reminder>x</system-reminder> the question")).Asked);
        }

        public static void Survives_broken_and_unrelated_lines()
        {
            var d = Read(10, "{'type':'user','message':{'content':'cut o", "not json", "{'type':'progress'}", "", User("still read"));
            Check.Equal("still read", d.Asked);
        }

        public static void Keeps_only_the_latest_events()
        {
            var d = Read(2, User("one"), User("two"), User("three"));
            Check.Equal(2, d.Events.Count);
            Check.Equal("two", d.Events[0].Text);
        }

        public static void Knows_what_is_still_running()
        {
            var d = Read(10, Tool("t1", "Bash", "{'description':'Run the tests','command':'secret-token'}"));
            Check.Equal("running Bash (Run the tests)", d.Now);
            Check.HasNot(d.Now, "secret-token");
        }

        public static void A_finished_tool_is_no_longer_running()
        {
            Check.Equal("thinking", Read(10, Tool("t1", "Bash", "{'command':'x'}"), Result("t1")).Now);
        }

        public static void Lists_running_subagents_only()
        {
            var d = Read(10,
                Tool("a1", "Agent", "{'description':'Done one'}"), Result("a1"),
                Tool("a2", "Task", "{'description':'Still going'}"));
            Check.Equal(1, d.Subagents.Count);
            Check.Has(d.Subagents[0], "Still going");
        }

        public static void Replays_the_task_list()
        {
            var d = Read(10,
                Tool("p", "mcp__ccd_turn__update_plan", "{'items':[{'content':'one','status':'in_progress'},{'content':'two','status':'pending'}]}"),
                Tool("q", "mcp__ccd_turn__update_plan", "{'advance':true}"));
            Check.Equal(2, d.Tasks.Count);
            Check.Equal("completed", d.Tasks[0].Value);
            Check.Equal("in_progress", d.Tasks[1].Value);
            Check.Equal("thinking", d.Now); // the plan tools aren't work
        }

        public static void Reads_the_title()
        {
            Check.Equal("My chat", Read(10, "{'type':'custom-title','customTitle':'My chat'}").Title);
        }

        public static void Tool_names_are_shortened()
        {
            Check.Equal("running send_message", Read(10, Tool("t", "mcp__gmail__send_message", "{}")).Now);
            Check.Equal("running Read (a.txt)", Read(10, Tool("t", "Read", "{'file_path':'C:/x/a.txt'}")).Now);
        }

        public static void Project_is_the_folder_or_the_repository_of_a_worktree()
        {
            Check.Equal("repo", SessionBrief.Project(@"C:\work\repo"));
            Check.Equal("repo", SessionBrief.Project("C:/work/repo/"));
            Check.Equal("repo", SessionBrief.Project(@"C:\work\repo\.claude\worktrees\brave-fox"));
            Check.Equal("", SessionBrief.Project(""));
            Check.Equal("", SessionBrief.Project(null));
        }

        public static void Short_squeezes_space_and_cuts_long_text()
        {
            Check.Equal("a b", SessionBrief.Short(" a \n\t b ", 10));
            Check.Equal("abc…", SessionBrief.Short("abcdef", 3));
            Check.Equal("", SessionBrief.Short(null, 3));
        }
    }
}
