# Claude Notch

A small Windows companion for Claude: the Claude desktop app and Claude Code in a terminal.
A notch on the edge of your screen shows what your sessions are doing, lets you answer
permission requests, and lets you talk or type to Claude from anywhere.

## What it does

- **Live sessions.** Every running Claude Code session, desktop or terminal, is read from
  `~/.claude/sessions`. The notch shows the one you just left (or the one working) with its
  name, working time, and tasks done and left: `Working 4:12 · 3 done · 2 left`. Hover over it
  to see all sessions (a laptop for app sessions, a terminal for CLI ones, with their project), and click one to jump to it. A terminal session brings its terminal
  window forward. An app session opens Claude and selects it in the sidebar.
- **Permission requests.** When a Claude app session needs permission while you're away from it,
  the notch shows what it wants to run (`Bash: git push`) with **Allow** and **Deny**. If you go
  back to the session instead, Claude asks in its own window as usual. A terminal (CLI) session
  asks in the terminal right away; the notch shows a small notice with **Open terminal**.
- **Claude's questions.** When a Claude app session ends its turn with a question ("Should I test
  …?") and buttons, the notch shows the same question with the same buttons, the recommended one
  in clay. Clicking one presses that button in Claude. If it can't be pressed, a reply button's text
  is sent to the session as your answer, and an app action (Commit, Create PR, …) opens Claude so
  you can press it there. It goes away once you answer, in the notch or in Claude.
- **Voice.** The shortcut (the Copilot key by default) starts a Claude voice conversation in the
  open chat (or a new one) and minimizes Claude, so you talk through the notch. The notch shows a
  timer, a mute button, and live bars. Its **×** ends the conversation.
- **Quick voice.** Push to talk, without opening Claude. Hold the shortcut while you speak (or
  press it once and talk); let go and the question goes to a Claude Code session running in the
  background. The answer streams onto the notch and is read aloud with a Windows voice, sentence
  by sentence. Press the shortcut again to cut it off and ask something else. Answers follow the
  `voice-replies` skill (`~/.claude/skills/voice-replies/SKILL.md`, written on first use and
  yours to edit): short, plain spoken sentences, no markdown or links. Needs Claude Code signed
  in (`claude auth login`).
  - *Speech recognition:* **built in** runs [whisper.cpp](https://github.com/ggml-org/whisper.cpp)
    (MIT) with an English Whisper model on your PC, downloaded once from settings (Accurate,
    190 MB, or Fast, 60 MB) and checked against its SHA-256; nothing leaves the computer. Or
    **Windows**: its online speech recognition (the Win+H engine) when that's turned on in
    Settings → Privacy & security → Speech, else the older offline engine.
  - *What it can use:* web search only (fastest), or **your skills and connectors**, like a normal
    session. Then anything beyond looking things up (sending, creating, running commands) waits
    for **Allow** on the notch.
  - *History:* private (nothing saved; follow-ups keep context for 15 minutes), or **one chat,
    VOICE_CHATS**, in your Claude Code history, picked up again each time.
- **Typing.** If you prefer typing, or there's no microphone for Claude voice, the shortcut opens
  the Claude app's own quick entry (it presses Ctrl+Alt+Space). If Claude isn't running, it
  starts on a new chat.
- **Styles.** *Classic* shows the mascot and a status line. *Compact* is one slim line. *Line* is a
  thin colored bar, and *Dot* is a small colored dot; hover either one for details. It can sit on
  any edge of any display. Drag it and it snaps to the nearest edge.
- **Stays out of the way.** It hides while you're looking at Claude (the app, or the terminal
  running the session) and over fullscreen apps. **×** hides it until you go back to Claude, or for
  15 minutes or an hour.
- **Tray icon.** Settings, *Type to Claude*, *Repair hooks*, and *Quit*.

## Install

Run `ClaudeNotch.exe` (build it first, see below). It installs itself:

1. Copies itself to `%LOCALAPPDATA%\ClaudeNotch`.
2. Starts with Windows, through the `HKCU\...\Run` key.
3. Adds its hooks to `~/.claude/settings.json`. The original file is kept as
   `settings.json.claudenotch-backup`.
4. Starts.

Running a newer exe the same way updates it. Running it while it's already running opens the
settings.

Note that Claude Code reads hooks when a session starts, so sessions that were already open only
report task counts and permission requests after they restart. Session status and working time
work for every session right away.

### Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1     # -> bin\ClaudeNotch.exe
powershell -ExecutionPolicy Bypass -File .\install.ps1   # build, then run it (installs/updates)
```

No SDK is needed. It compiles with the C# compiler that ships with Windows (.NET Framework 4.x).

Other ways to run it:

| Command | What it does |
| --- | --- |
| `ClaudeNotch.exe --here` | Run from where it is, without installing. Use this for testing a build. |
| `ClaudeNotch.exe --demo` | Preview the notch's states without touching Claude (about 40 s). |
| `ClaudeNotch.exe --settings` | Open the settings of the running copy. |
| `ClaudeNotch.exe --uninstall` | Remove the app, its hooks, and its startup entry. |

## Uninstall

Use **Settings → Uninstall…**, or run `uninstall.ps1`. Only Claude Notch's own hooks are removed
from `~/.claude/settings.json`; everything else in that file stays as it is.

## Settings

| Section | Options |
| --- | --- |
| General | Start with Windows. Answer permission requests from the notch. Show Claude's questions with their choices. Keep the hooks installed (repaired automatically every 5 minutes if something removes them). |
| Shortcut | Copilot key, your own key combination, or none. |
| Voice and prompts | Quick voice, Claude voice (with Claude's quick entry as the fallback without a mic), or always Claude's quick entry. Quick voice: speech recognition (built in or Windows), what it can use, history, model, reading answers aloud, the voice and its speed, and editing the answer style. |
| Notch | Style, screen edge, display, position along the edge, whether to list sessions on hover. |
| When it shows | Only when something's going on (or Claude is minimized), whenever you're away from Claude, or only for permission requests and voice. How long × hides it. How long "Done" stays. Whether to show over fullscreen apps. |

Settings are saved in `%LOCALAPPDATA%\ClaudeNotch\settings.json`. Problems are written to
`ClaudeNotch.log` in the same folder.

## How it works

```
Claude Code hooks ──stdin──> ClaudeNotch.exe --hook ──named pipe──> running notch
                   <─stdout── (allow / deny for PermissionRequest)
~/.claude/sessions/*.json ──polled every second──> session list
Claude window ──UI Automation──> voice, mute, current session title, prompt box
```

- **The pipe** is `\\.\pipe\ClaudeNotch-<your user SID>`. Only your Windows user can open it, and
  network clients are refused. The hook checks that the process on the other end is Claude Notch
  before sending anything. If the notch isn't running, the hook prints nothing and exits, so
  Claude Code behaves as if there were no hook.
- **Permission requests** are held for at most 9 minutes (the hook's timeout is 10). They are
  handed back to Claude's own prompt when you look at the session, when the session moves on,
  or when you hide the notch.

| File | What it does |
| --- | --- |
| `src/Program.cs` | Entry point and command-line modes; the demo. |
| `src/Controller.cs` | Ties it together: what the notch shows and when, permission requests, the shortcut. |
| `src/HookServer.cs` | The named pipe, both ends (`--hook` relay and the server in the notch). |
| `src/HookInstaller.cs` | Adds, repairs, and removes the hooks in `~/.claude/settings.json`. |
| `src/Installer.cs` | Self-install, startup entry, update, uninstall. |
| `src/SessionScanner.cs` | Reads the live session files and checks each session's process is really alive. |
| `src/SessionTracker.cs` | Task lists and turn timing from hook events. |
| `src/SessionHub.cs` | Merges both into one session list. |
| `src/ClaudeWatcher.cs` | Claude's window: open, minimized, in front, voice, mute, current session, fullscreen apps. |
| `src/VoiceLauncher.cs` | Starts voice mode; microphone detection. |
| `src/QuickVoice.cs` | Quick voice: speech recognition, the background Claude Code session, reading answers aloud, the voice-replies skill. |
| `src/ModernDictation.cs` | Windows' modern (online) dictation, used by quick voice when allowed. |
| `src/LocalSpeech.cs`, `src/MicRecorder.cs` | The built-in speech recognition (whisper.cpp): download, checks, transcribing; recording the microphone. |
| `src/PromptSender.cs` | Sending text to the Claude app's prompt box; opening its quick entry. |
| `src/TerminalFocus.cs` | Finds the terminal window a CLI session runs in. |
| `src/Hotkeys.cs` | The Copilot key hook and custom shortcuts. |
| `src/NotchWindow.cs`, `src/Mascot.cs` | The notch and its pixel mascot. |
| `src/SettingsWindow.cs`, `src/TrayIcon.cs` | Settings and the tray icon. |
| `src/Settings.cs`, `src/Json.cs`, `src/Log.cs`, `src/Native.cs`, `src/Theme.cs` | Shared pieces. |
