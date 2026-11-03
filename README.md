# Claude Notch

A small Windows companion for the Claude desktop app.

- **Notch:** while Claude is open but not the window in front (minimized or Alt+Tabbed away), a black pill shows with an animated pixel mascot. It stays hidden while a fullscreen app is in front.
- **Voice indicator:** during a Claude voice conversation, the notch widens and shows "Listening", a timer, live bars, and a mute button. The mute button presses Claude's own mic toggle. The mascot hops around.
- **Permission alerts:** when Claude Code asks for permission, the notch shows the project and what it wants to run (e.g. `Bash: git push`). The mascot wiggles and an amber dot pulses. The alert goes away a few seconds after you switch back to Claude.
- **Copilot key:** pressing the Copilot key opens a new Claude chat and starts voice mode. Once voice is up, it minimizes Claude so you talk through the notch.

## Using the notch

| Action | What happens |
| --- | --- |
| Click | Brings Claude to the front |
| Drag | Moves it anywhere. Drop it near the top edge to snap it back into a notch. The position is remembered. |
| Hover, then **x** | Hides it until you next switch to Claude |
| Right-click | Open Claude, Hide for now, Move back to top center, Quit |

## Install

From PowerShell in this folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This builds the app, copies it to `%LOCALAPPDATA%\ClaudeNotch`, adds it to Windows startup, and starts it.
To build without installing, run `build.ps1`. The output goes to `bin\ClaudeNotch.exe`.
To preview every state without touching Claude, run `bin\ClaudeNotch.exe --demo`.

No SDK is needed. It compiles with the C# compiler that ships with Windows (.NET Framework 4.x).

### Permission alerts (Claude Code hooks)

Add this to `~/.claude/settings.json`. The hook passes the event to the running notch and exits without output, so Claude's normal permission prompt is unchanged.

```json
"hooks": {
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

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Remove the hooks from `~/.claude/settings.json` too.

## How it works

| File | What it does |
| --- | --- |
| `src/Program.cs` | Entry point; single instance; `--hook` relay and the named pipe it talks to; `--demo`. |
| `src/ClaudeWatcher.cs` | Polls whether Claude is open, in front, or covered by a fullscreen app. It detects voice and mute by looking for Claude's "Turn off/on microphone" button through UI Automation. |
| `src/NotchWindow.cs` | The notch: idle, voice, and alert layouts; drag/snap; menu; animations. |
| `src/Mascot.cs` | The pixel mascot and its idle, talking, and alert animations. |
| `src/CopilotKey.cs` | Low-level keyboard hook for the Copilot key (Win + Shift + F23). It opens `claude://claude.ai/new`, presses "Use voice mode" through UI Automation, and minimizes Claude once voice is up. |

## Config

Everything lives in `%LOCALAPPDATA%\ClaudeNotch`:

- `ClaudeNotch.txt` changes what the Copilot key opens. For example, `https://claude.ai/new` opens the browser instead. Voice auto-start only works with the desktop app.
- `position.txt` stores where you dragged the notch. Delete it to reset.
- `ClaudeNotch.log` lists the buttons it found if voice mode didn't start.
