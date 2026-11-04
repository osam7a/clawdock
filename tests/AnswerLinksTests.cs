using System;
using System.Linq;

namespace Clawdock.Tests
{
    static class AnswerLinksTests
    {
        static string Show(string text)
        {
            return string.Join("|", AnswerLinks.Split(text).Select(p => p.Url == null ? p.Text : "<" + p.Text + "=" + p.Url + ">"));
        }

        public static void Text_without_links_stays_one_piece()
        {
            Check.Equal("Just words.", Show("Just words."));
            Check.Equal("", Show(""));
            Check.Equal("", Show(null));
        }

        public static void Labelled_and_bare_links_are_found()
        {
            Check.Equal("See |<the docs=https://x.io/a>| now", Show("See [the docs](https://x.io/a) now"));
            Check.Equal("Go to |<https://x.io/a=https://x.io/a>|.", Show("Go to https://x.io/a."));
            Check.Equal("(|<http://x.io/a=http://x.io/a>|)", Show("(http://x.io/a)"));
            Check.Equal("<https://x.io/a_(b)=https://x.io/a_(b)>", Show("https://x.io/a_(b)"));
        }

        public static void Only_web_links_can_open()
        {
            foreach (string bad in new[] { "file:///C:/Windows/System32/calc.exe", "javascript:alert(1)", "C:\\Windows\\System32\\calc.exe", "ms-settings:privacy", "claude://claude.ai/new", "", null })
                Check.Equal(null, AnswerLinks.SafeUrl(bad), bad ?? "null");
            Check.Equal("run this", Show("[run this](file:///C:/x.exe)"));

            string opened = null;
            var real = AnswerLinks.Opener;
            AnswerLinks.Opener = u => opened = u;
            try
            {
                AnswerLinks.Open("file:///C:/x.exe");
                Check.Equal(null, opened);
                AnswerLinks.Open("https://x.io/a");
                Check.Equal("https://x.io/a", opened);
            }
            finally { AnswerLinks.Opener = real; }
        }

        public static void Display_keeps_web_links_whole()
        {
            Check.Equal("[a](https://x.io/b__c)", QuickVoice.Display("[a](https://x.io/b__c)"));
            Check.Equal("At https://x.io/b__c today", QuickVoice.Display("At **https://x.io/b__c** today"));
            Check.Equal("run this", QuickVoice.Display("[run this](file:///C:/x.exe)"));
        }

        public static void A_link_still_arriving_shows_only_its_label()
        {
            Check.Equal("See the docs", QuickVoice.Display("See [the docs](https://x.i"));
        }

        public static void Links_are_not_read_aloud()
        {
            Check.Equal("See the docs now", QuickVoice.Speakable("See [the docs](https://x.io/a) now"));
            Check.Equal("Go to now", QuickVoice.Speakable("Go to https://x.io/a now"));
        }
    }
}
