using System;
using System.IO;

namespace ClaudeNotch.Tests
{
    static class ProjectStarterTests
    {
        public static void Clean_name_is_always_a_single_folder_name()
        {
            Check.Equal("My App 2", ProjectStarter.CleanName("My App 2"));
            Check.Equal("name", ProjectStarter.CleanName("  name. . "));
            Check.Equal("abc", ProjectStarter.CleanName("a\\b:c*"));
            Check.Equal("....evil", ProjectStarter.CleanName("../../evil"));
            Check.Equal("ab", ProjectStarter.CleanName("a\u0007\nb"));
        }

        public static void Clean_name_refuses_what_cant_be_a_folder()
        {
            foreach (string bad in new[] { null, "", "   ", ".", "..", "...", "///", "CON", "nul", "Com1", "LPT9", "CON.txt", "nul.tar.gz", "aux." })
                Check.Equal("", ProjectStarter.CleanName(bad), "[" + bad + "]");
        }

        public static void Clean_name_keeps_names_that_only_look_reserved()
        {
            foreach (string fine in new[] { "CONsole", "com10", "Auxiliary", "nullable" })
                Check.Equal(fine, ProjectStarter.CleanName(fine));
        }

        public static void Clean_name_is_cut_to_a_sensible_length()
        {
            Check.Equal(60, ProjectStarter.CleanName(new string('a', 200)).Length);
            Check.Equal(new string('a', 59), ProjectStarter.CleanName(new string('a', 59) + ". more"));
        }

        public static void Is_free_only_when_nothing_would_be_mixed_in()
        {
            string dir = Path.Combine(Path.GetTempPath(), "notch-test-" + Guid.NewGuid());
            try
            {
                Check.That(ProjectStarter.IsFree(dir), "a missing folder is free");
                Directory.CreateDirectory(dir);
                Check.That(ProjectStarter.IsFree(dir), "an empty folder is free");
                string file = Path.Combine(dir, "a.txt");
                File.WriteAllText(file, "x");
                Check.That(!ProjectStarter.IsFree(dir), "a folder with something in it isn't");
                Check.That(!ProjectStarter.IsFree(file), "a file isn't");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
