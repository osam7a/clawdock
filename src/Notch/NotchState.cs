using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Clawdock
{
    // What the notch should show; built by the Controller every refresh.
    class NotchState
    {
        public bool Visible;
        public string Kind = "idle";        // idle | session | voice | alert | question | quick
        public bool Listening, Answering;   // quick voice: hearing you / reading out the answer
        public QuickTurn QuickNow;          // quick voice: the turn in progress, and the ones before it
        public List<QuickTurn> QuickHistory = new List<QuickTurn>();
        public string QuickStatus = "";     // "Thinking…", "Searching the web…", an error
        public bool QuickMinimized, Speaking; // quick voice as a small bar; the answer is being read out
        public bool QuickCanClear;            // a private chat, which can be cleared
        public bool QuickTyping;              // the text box is open
        public MascotMood Mood;               // what the mascot acts out
        public string Title = "Claude", Detail = "", Short = ""; // Short: "4:12 · 3/5", for the compact style
        public bool Busy, JustFinished, Muted;
        public int Done, Total;
        public DateTime VoiceStarted;
        public HookServer.Pending Pending;  // a request waiting for Allow / Deny
        public int MorePending;
        // Buttons under the text: Deny / Allow for a permission request, or the choices of a
        // question Claude asked. Owner is what they answer; Primary is drawn in clay.
        public object Owner;
        public List<string> Choices = new List<string>();
        public int Primary = -1;
        public List<SessionInfo> Sessions = new List<SessionInfo>();
    }
}
