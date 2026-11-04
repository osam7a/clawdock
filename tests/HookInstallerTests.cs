using System.Collections;
using System.Collections.Generic;

namespace ClaudeNotch.Tests
{
    // Only the merge: Install and Uninstall write to the real ~/.claude/settings.json.
    static class HookInstallerTests
    {
        const string Exe = @"C:\Apps\ClaudeNotch.exe";

        static IList Groups(Dictionary<string, object> root, string ev) { return Json.Obj(root, "hooks")[ev] as IList; }

        static Dictionary<string, object> Add(string json) { return HookInstaller.Merge(Check.Obj(json), Exe, true); }
        static Dictionary<string, object> Remove(string json) { return HookInstaller.Merge(Check.Obj(json), null, false); }

        public static void Command_uses_forward_slashes_and_quotes()
        {
            Check.Equal("\"C:/Apps/Claude Notch/ClaudeNotch.exe\" --hook", HookInstaller.Command(@"C:\Apps\Claude Notch\ClaudeNotch.exe"));
        }

        public static void Adds_a_hook_for_every_event()
        {
            var root = Add("{}");
            foreach (string ev in new[] { "UserPromptSubmit", "Stop", "SessionEnd", "PostToolUse", "Notification", "PermissionRequest" })
                Check.Equal(1, Groups(root, ev).Count, ev);
            Check.Equal(6, HookInstaller.CountOurs(root));
            Check.Has(Json.Write(root), "C:/Apps/ClaudeNotch.exe");
        }

        public static void Permission_requests_wait_long_enough_to_answer()
        {
            var group = Groups(Add("{}"), "PermissionRequest")[0] as Dictionary<string, object>;
            Check.Equal("*", Json.Str(group, "matcher"));
            Check.Equal(600L, Json.Long((group["hooks"] as IList)[0] as Dictionary<string, object>, "timeout"));
        }

        public static void Leaves_everything_else_in_the_file_alone()
        {
            string text = Json.Write(Add("{'model':'opus','hooks':{'Stop':[{'hooks':[{'type':'command','command':'my-script.cmd'}]}],'PreCompact':[{'hooks':[{'type':'command','command':'other.cmd'}]}]}}"));
            Check.Has(text, "\"model\": \"opus\"");
            Check.Has(text, "my-script.cmd");
            Check.Has(text, "other.cmd");
        }

        public static void Adding_twice_changes_nothing()
        {
            var once = Add("{'hooks':{'Stop':[{'hooks':[{'type':'command','command':'mine.cmd'}]}]}}");
            string first = Json.Write(once);
            var again = HookInstaller.Merge(Json.ParseObject(first), Exe, true);
            Check.Equal(first, Json.Write(again));
            Check.Equal(6, HookInstaller.CountOurs(again));
        }

        public static void Replaces_a_hook_from_an_old_install_or_the_old_name()
        {
            foreach (string old in new[] { "C:/Old/ClaudeNotch.exe --hook", "C:/Old/Wrenlet.exe --hook" })
            {
                var root = Add("{'hooks':{'Stop':[{'hooks':[{'type':'command','command':'" + old + "'}]}]}}");
                Check.HasNot(Json.Write(root), "C:/Old/");
                Check.Equal(1, Groups(root, "Stop").Count);
            }
        }

        public static void Never_counts_someone_elses_hook_as_ours()
        {
            foreach (string other in new[] { "other.exe --hook", "ClaudeNotch.exe --version", "notify.cmd" })
            {
                string json = "{'hooks':{'Stop':[{'hooks':[{'type':'command','command':'" + other + "'}]}]}}";
                Check.Equal(0, HookInstaller.CountOurs(Check.Obj(json)), other);
                Check.Has(Json.Write(Remove(json)), other);
            }
        }

        public static void Uninstall_removes_ours_and_the_empty_parts_left_behind()
        {
            var root = Add("{'model':'opus'}");
            var clean = HookInstaller.Merge(root, null, false);
            Check.That(!clean.ContainsKey("hooks"), "no empty hooks left: " + Json.Write(clean));
            Check.Equal("opus", Json.Str(clean, "model"));
        }

        public static void Uninstall_keeps_a_neighbor_in_the_same_group()
        {
            var root = Remove("{'hooks':{'Stop':[{'matcher':'x','hooks':[{'type':'command','command':'ClaudeNotch.exe --hook'},{'type':'command','command':'mine.cmd'}]}]}}");
            string text = Json.Write(root);
            Check.Has(text, "mine.cmd");
            Check.Has(text, "\"matcher\": \"x\"");
            Check.HasNot(text, "ClaudeNotch");
        }

        public static void Survives_a_strange_file()
        {
            string text = Json.Write(Add("{'hooks':{'Odd':'not a list','Stop':['not a group',{'hooks':'not a list'}]}}"));
            Check.Has(text, "not a list");
            Check.Has(text, "not a group");
        }
    }
}
