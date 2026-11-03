# Claude Notch

A small Windows companion for the Claude desktop app.

- **Notch:** when you minimize Claude, a black pill drops down from the top of the screen with an animated pixel mascot. Click it to bring Claude back.
- **Voice indicator:** while Claude is using your microphone, the notch widens and shows "Listening", a timer, and live bars. The mascot hops around.
- **Copilot key:** pressing the Copilot key opens a new Claude chat and starts voice mode.

## Install

From PowerShell in this folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This builds the app, copies it to `%LOCALAPPDATA%\ClaudeNotch`, adds it to Windows startup, and starts it.
To build without installing, run `build.ps1`. The output goes to `bin\ClaudeNotch.exe`.
To preview the notch without minimizing Claude, run `bin\ClaudeNotch.exe --demo`. It shows the idle notch, switches to voice, then slides away after about 20 seconds.

No SDK is needed. It compiles with the C# compiler that ships with Windows (.NET Framework 4.x).

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

## How it works

| File | What it does |
| --- | --- |
| `src/Program.cs` | Entry point; single instance; wires everything together. |
| `src/ClaudeWatcher.cs` | Polls the Claude window to see whether it's minimized. It detects voice through the Windows microphone-usage record (`CapabilityAccessManager\ConsentStore\microphone`). |
| `src/NotchWindow.cs` | The notch: a borderless, topmost, non-activating window with slide and resize animations. |
| `src/Mascot.cs` | The pixel mascot and its idle and talking animations. |
| `src/CopilotKey.cs` | Low-level keyboard hook for the Copilot key (Win + Shift + F23). It opens `claude://claude.ai/new` and presses the voice mode button through UI Automation. |

## Config

- Put a different URL in `%LOCALAPPDATA%\ClaudeNotch\ClaudeNotch.txt` to change what the Copilot key opens. For example, `https://claude.ai/new` opens the browser instead. Voice auto-start only works with the desktop app.
- If voice mode doesn't start, `ClaudeNotch.log` in the same folder lists the buttons it found.
