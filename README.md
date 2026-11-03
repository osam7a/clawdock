# Claude Notch

A small Windows companion for the Claude desktop app.

- **Notch:** a black pill with an animated pixel mascot that shows while Claude is open but not the window in front.
  - *Minimized:* it always shows.
  - *Alt+Tabbed away:* it only shows while something is going on (a voice conversation, a session that's working, or a permission request). If nothing is happening, it stays hidden.
  - It stays hidden over fullscreen apps.
- **Session info:** when you leave a Claude Code session, the notch shows its name, how long it has been working, and how many tasks are done and left (`Working 4:12 · 3 done · 2 left`, with a thin progress bar). When the session finishes it shows `Done in 4:12` for a few seconds. If you minimized, the last session stays on the notch for 30 minutes.
- **Voice indicator:** during a Claude voice conversation, the notch widens and shows "Listening", a timer, live bars, and a mute button. The mute button presses Claude's own mic toggle. The mascot hops around.
- **Permission alerts:** when Claude Code asks for permission, the notch shows the project and what it wants to run (e.g. `Bash: git push`). The mascot wiggles and an amber dot pulses. The alert goes away a few seconds after you switch back to Claude, or when the session moves on.
- **Copilot key:** starts a Claude voice conversation, then minimizes Claude so you talk through the notch.
  - If a chat is already open, voice continues in that chat. A new chat is only opened if the current view has no voice button (for example the Code tab), or if Claude isn't running.
  - If voice is already running, pressing it does nothing.

## Using the notch

| Action | What happens |
| --- | --- |
| Click | Brings Claude to the front |
| Drag | Moves it anywhere. Drop it near the top edge to snap it back into a notch. The position is remembered. |
| Hover, then **x** | Hides it until you next switch to Claude. During a voice conversation it also ends the conversation. |
| Mic button | Mutes or unmutes Claude (voice only) |
| Right-click | Open Claude, End voice conversation, Hide for now, Move back to top center, Quit |

## Install

From PowerShell in this folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This builds the app, copies it to `%LOCALAPPDATA%\ClaudeNotch`, adds it to Windows startup, and starts it.
To build without installing, run `build.ps1`. The output goes to `bin\ClaudeNotch.exe`.
To preview every state without touching Claude, run `bin\ClaudeNotch.exe --demo` (about 40 seconds).

No SDK is needed. It compiles with the C# compiler that ships with Windows (.NET Framework 4.x).

### Claude Code hooks (permission alerts and session info)

Add this to `~/.claude/settings.json`, using your own path. Each hook passes the event to the running notch and exits without output, so Claude Code behaves exactly as before.

```json
"hooks": {
  "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "\"C:/Users/<you>/AppData/Local/ClaudeNotch/ClaudeNotch.exe\" --hook", "timeout": 5 }] }],
  "Stop":             [{ "hooks": [{ "type": "command", "command": "\"C:/Users/<you>/AppData/Local/ClaudeNotch/ClaudeNotch.exe\" --hook", "timeout": 5 }] }],
  "SessionEnd":       [{ "hooks": [{ "type": "command", "command": "\"C:/Users/<you>/AppData/Local/ClaudeNotch/ClaudeNotch.exe\" --hook", "timeout": 5 }] }],
  "PostToolUse": [
    { "matcher": "mcp__ccd_turn__update_plan|mcp__ccd_turn__wrap_up|TodoWrite",
      "hooks": [{ "type": "command", "command": "\"C:/Users/<you>/AppData/Local/ClaudeNotch/ClaudeNotch.exe\" --hook", "timeout": 5 }] }
  ],
  "Notification": [
    { "matcher": "permission_prompt",
      "hooks": [{ "type": "command", "command": "\"C:/Users/<you>/AppData/Local/ClaudeNotch/ClaudeNotch.exe\" --hook", "timeout": 5 }] }
  ],
  "PermissionRequest": [
    { "matcher": "*",
      "hooks": [{ "type": "command", "command": "\"C:/Users/<you>/AppData/Local/ClaudeNotch/ClaudeNotch.exe\" --hook", "timeout": 5 }] }
  ]
}
```

- `UserPromptSubmit` and `Stop` give the working time.
- The `PostToolUse` hook on the task tools gives the done/left counts.
- `SessionEnd` clears a session.
- `Notification` and `PermissionRequest` give the permission alerts.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Remove the hooks from `~/.claude/settings.json` too.

## How it works

| File | What it does |
| --- | --- |
| `src/Program.cs` | Entry point; single instance; `--hook` relay and the named pipe it talks to; `--demo`. |
| `src/ClaudeWatcher.cs` | Polls whether Claude is open, minimized, in front, or covered by a fullscreen app. It detects voice and mute by looking for Claude's "Turn off/on microphone" button through UI Automation, reads the current session's name from the header, and ends voice. |
| `src/SessionTracker.cs` | Builds each session's working time and task list from the hook events. |
| `src/NotchWindow.cs` | The notch: alert, voice, session and idle layouts; show/hide rules; drag/snap; menu; animations. |
| `src/Mascot.cs` | The pixel mascot and its idle, talking, and alert animations. |
| `src/CopilotKey.cs` | Low-level keyboard hook for the Copilot key (Win + Shift + F23). It presses "Use voice mode" through UI Automation and minimizes Claude once voice is up. |

## Config

Everything lives in `%LOCALAPPDATA%\ClaudeNotch`:

- `ClaudeNotch.txt` changes the link the Copilot key opens for a new chat. For example, `https://claude.ai/new` opens the browser instead. Voice auto-start only works with the desktop app.
- `position.txt` stores where you dragged the notch. Delete it to reset.
- `ClaudeNotch.log` is written when something fails. If voice mode doesn't start, it lists the buttons it found. If the x can't end a voice conversation, it lists the controls next to the mic toggle.
