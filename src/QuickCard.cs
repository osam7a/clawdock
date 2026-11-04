using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // One question and its answer, as the notch shows them.
    class QuickTurn
    {
        public string Heard = "", Text = ""; // what you said; the answer without its card
        public QuickCard Card;
        public bool Cut;                      // you cut the answer off
        public List<string> Notes = new List<string>(); // what it asked permission for, and what came of it
    }

    // The key fact of an answer, shown big. Claude sends it as <card>{json}</card> before the
    // spoken answer. Kind is one of:
    //   value    one figure ("$1,249") with a label and a note; optionally a change ("+2.4%") and
    //            a progress bar (0 to 1)
    //   list     a few rows, name and value
    //   steps    numbered steps
    //   compare  two or three things side by side, one row per attribute
    //   weather  the temperature now with its condition, and a few days ahead
    class QuickCard
    {
        public string Kind = "value", Source = "", Title = "", Value = "", Label = "", Note = "", Change = "", Weather = "";
        public double Progress = -1; // -1: no bar
        public List<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>();
        public List<string> Steps = new List<string>();
        public List<string> Columns = new List<string>();
        public List<string[]> Rows = new List<string[]>();  // compare: [attribute, one value per column]
        public List<string[]> Days = new List<string[]>();  // weather: [day, condition, high, low]

        // Whether the change reads as up (true), down (false) or neither (null).
        public bool? ChangeUp
        {
            get
            {
                string c = Change.TrimStart();
                if (c == "") return null;
                if (c[0] == '+' || c[0] == '↑' || c[0] == '▲') return true;
                if (c[0] == '-' || c[0] == '−' || c[0] == '↓' || c[0] == '▼') return false;
                return null;
            }
        }
    }
}
