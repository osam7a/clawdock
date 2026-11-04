using System;

namespace ClaudeNotch.Tests
{
    static class SessionTrackerTests
    {
        static void Plan(SessionTracker t, string input)
        {
            t.Apply(Check.Obj("{'hook_event_name':'PostToolUse','session_id':'a','tool_name':'mcp__ccd_turn__update_plan','tool_input':" + input + "}"));
        }

        const string ThreeSteps = "{'items':[{'content':'one','status':'in_progress'},{'content':'two','status':'pending'},{'content':'three','status':'pending'}]}";

        static SessionTracker.Snapshot Now(SessionTracker t) { return t.For("a", DateTime.Now); }

        static SessionTracker Started()
        {
            var t = new SessionTracker();
            t.Apply(Check.Obj("{'hook_event_name':'UserPromptSubmit','session_id':'a','cwd':'C:\\\\work\\\\repo'}"));
            return t;
        }

        public static void A_prompt_starts_a_turn_and_stop_ends_it()
        {
            var t = Started();
            Check.That(Now(t).Busy, "busy after a prompt");
            Check.Equal("repo", Now(t).Project);
            t.Apply(Check.Obj("{'hook_event_name':'Stop','session_id':'a'}"));
            Check.That(!Now(t).Busy, "not busy after stop");
        }

        public static void Session_end_forgets_it()
        {
            var t = Started();
            t.Apply(Check.Obj("{'hook_event_name':'SessionEnd','session_id':'a'}"));
            Check.That(Now(t) == null, "forgotten");
        }

        public static void Events_with_no_name_are_ignored()
        {
            var t = new SessionTracker();
            t.Apply(Check.Obj("{'session_id':'a'}"));
            Check.That(Now(t) == null, "nothing tracked");
        }

        public static void A_turn_with_no_news_for_hours_counts_as_over()
        {
            var t = Started();
            Check.That(!t.For("a", DateTime.Now.AddHours(3)).Busy, "expired");
        }

        public static void Joining_mid_turn_counts_as_working()
        {
            var t = new SessionTracker();
            t.Apply(Check.Obj("{'hook_event_name':'PostToolUse','session_id':'a','tool_name':'Read','tool_input':{}}"));
            Check.That(Now(t).Busy, "busy");
        }

        public static void Current_prefers_a_working_session()
        {
            var t = Started();
            t.Apply(Check.Obj("{'hook_event_name':'Stop','session_id':'b'}"));
            Check.Equal("a", t.Current(DateTime.Now).Id);
            Check.That(new SessionTracker().Current(DateTime.Now) == null, "nothing to talk about");
        }

        public static void Plan_counts_steps()
        {
            var t = Started();
            Plan(t, ThreeSteps);
            Check.Equal(3, Now(t).Total);
            Check.Equal(0, Now(t).Done);
        }

        public static void Advance_moves_to_the_next_step()
        {
            var t = Started();
            Plan(t, ThreeSteps);
            Plan(t, "{'advance':true}");
            Check.Equal(1, Now(t).Done);
            Plan(t, "{'advance':true}");
            Plan(t, "{'advance':true}");
            Check.Equal(3, Now(t).Done);
            Plan(t, "{'advance':true}"); // past the end
            Check.Equal(3, Now(t).Done);
        }

        public static void Done_and_start_use_numbers_from_one_and_ignore_bad_ones()
        {
            var t = Started();
            Plan(t, ThreeSteps);
            Plan(t, "{'done':[1,3,99,0],'start':2}");
            Check.Equal(2, Now(t).Done);
        }

        public static void Wrap_up_checks_off_the_list()
        {
            var t = Started();
            Plan(t, ThreeSteps);
            t.Apply(Check.Obj("{'hook_event_name':'PostToolUse','session_id':'a','tool_name':'mcp__ccd_turn__wrap_up','tool_input':{}}"));
            Check.Equal(3, Now(t).Done);
        }

        public static void A_new_prompt_clears_the_plan_but_keeps_open_tasks()
        {
            var t = Started();
            Plan(t, ThreeSteps);
            t.Apply(Check.Obj("{'hook_event_name':'PostToolUse','session_id':'a','tool_name':'TaskCreate','tool_input':{'subject':'open one'}}"));
            t.Apply(Check.Obj("{'hook_event_name':'PostToolUse','session_id':'a','tool_name':'TaskCreate','tool_input':{'subject':'done one'}}"));
            t.Apply(Check.Obj("{'hook_event_name':'PostToolUse','session_id':'a','tool_name':'TaskUpdate','tool_input':{'taskId':'2','status':'completed'}}"));
            t.Apply(Check.Obj("{'hook_event_name':'UserPromptSubmit','session_id':'a'}"));
            Check.Equal(1, Now(t).Total);
            Check.Equal(0, Now(t).Done);
        }

        public static void Tasks_can_be_deleted()
        {
            var t = Started();
            t.Apply(Check.Obj("{'hook_event_name':'PostToolUse','session_id':'a','tool_name':'TaskCreate','tool_input':{'subject':'x'}}"));
            t.Apply(Check.Obj("{'hook_event_name':'PostToolUse','session_id':'a','tool_name':'TaskUpdate','tool_input':{'taskId':'1','status':'deleted'}}"));
            Check.Equal(0, Now(t).Total);
        }

        public static void Todo_write_replaces_the_list()
        {
            var t = Started();
            Plan(t, ThreeSteps);
            t.Apply(Check.Obj("{'hook_event_name':'PostToolUse','session_id':'a','tool_name':'TodoWrite','tool_input':{'todos':[{'content':'only','status':'completed'}]}}"));
            Check.Equal(1, Now(t).Total);
            Check.Equal(1, Now(t).Done);
        }

        public static void Clock_reads_minutes_and_hours()
        {
            Check.Equal("1:05", SessionTracker.Clock(TimeSpan.FromSeconds(65)));
            Check.Equal("1:01:40", SessionTracker.Clock(TimeSpan.FromSeconds(3700)));
            Check.Equal("26:00:00", SessionTracker.Clock(TimeSpan.FromHours(26)));
            Check.Equal("0:00", SessionTracker.Clock(TimeSpan.FromSeconds(-5)));
        }

        public static void Mascot_mood_follows_the_tool()
        {
            Check.Equal(MascotMood.Searching, SessionActivity.ForTool("WebSearch"));
            Check.Equal(MascotMood.Reading, SessionActivity.ForTool("Grep"));
            Check.Equal(MascotMood.Working, SessionActivity.ForTool("Bash"));
            Check.Equal(MascotMood.Working, SessionActivity.ForTool(""));
        }
    }
}
