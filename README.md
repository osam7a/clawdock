# Claude Notch

A small notch on the edge of your Windows screen for Claude. See what your sessions are doing,
answer permission requests and questions, and talk or type to Claude from anywhere.

It's one small exe with no dependencies, and it works with the Claude desktop app and Claude Code.

![The notch showing a session at work, and the list of sessions](docs/screenshots/sessions.png)

> Claude Notch is an independent community project. It isn't made, endorsed, or supported by
> Anthropic. "Claude" is a trademark of Anthropic.

## Install

You need Windows 10 or 11, and the [Claude desktop app](https://claude.ai/download) or
[Claude Code](https://claude.com/claude-code). Quick voice needs Claude Code, signed in.

**From a release:** download `ClaudeNotch.exe` from the
[latest release](https://github.com/osam7a/claude-notch/releases/latest) and run it. The exe isn't
code-signed yet, so if Windows SmartScreen warns you, choose **More info**, then **Run anyway**.

**From source:** no SDK needed, it builds with the compiler that ships with Windows.

```powershell
git clone https://github.com/osam7a/claude-notch
cd claude-notch
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Either way it installs itself to `%LOCALAPPDATA%\ClaudeNotch`, starts with Windows, and adds its
hooks to `~/.claude/settings.json`, keeping a backup of that file. To update, run a newer exe the
same way. Sessions that were already open report task counts and permission requests after they
restart.

## Features

### Follow your sessions

Every running session, in the app or a terminal, with its progress and time. Hover to see them all,
and click one to jump to it.

![A session at work, with its progress bar](docs/screenshots/working.png)

### Answer permission requests

Allow or deny from the notch while you're away from the chat.

![A permission request with Allow and Deny](docs/screenshots/permission.png)

Click the request to see exactly what it's asking for.

![The same request expanded to show the full command](docs/screenshots/permission-details.png)

### Answer Claude's questions

When a session ends with a question, it appears on the notch with its buttons.

![A question from Claude with two answers](docs/screenshots/question.png)

### Talk to Claude

Press the Copilot key or your own shortcut and just ask. Answers are read aloud, and speech
recognition and the reading voice can both run on your PC. Press Esc to cancel.

![Quick voice listening to a question](docs/screenshots/quick-listening.png)

The key fact of an answer shows up as a card: a number, a list, steps, a comparison, or the weather.

![A weather card](docs/screenshots/quick-weather.png)

![A card comparing two plans](docs/screenshots/quick-compare.png)

### Type to Claude

Double-press the shortcut for a small text box, and keep chatting there. You can move a long chat
into the Claude app to continue it.

![Quick typing with a message box](docs/screenshots/quick-typing.png)

### Start new projects

Ask for a new project and Claude picks a name, model and effort, and writes a proper first prompt.
Nothing starts until you press Allow. Then it makes the folder and opens a real session in the app.

![A new project waiting for Allow, with its folder, model, effort and first message](docs/screenshots/new-project.png)

### Ask about your sessions

Ask by voice what a session is doing, its task list, or whether a finished one got done, without
interrupting it. Sending a session a message always needs your OK.

### A mascot that acts it out

Clouds when it's thinking, a laptop when it's working, a magnifier when searching, and more.

![The mascot in each of its moods](docs/screenshots/mascot-moods.png)

## Styles

Pick a style, drag the notch to any edge of any display, and set its size at rest and when open.

| Style | Looks like |
| --- | --- |
| **Classic**: mascot and status | ![Classic style](docs/screenshots/style-classic.png) |
| **Compact**: one slim line | ![Compact style](docs/screenshots/style-compact.png) |
| **Line**: a thin bar, opens when Claude needs you | ![Line style](docs/screenshots/style-line.png) |
| **Dot**: a small dot, opens when Claude needs you | ![Dot style](docs/screenshots/style-dot.png) |

| Edge or size | Looks like |
| --- | --- |
| Left edge | ![The notch on the left edge](docs/screenshots/edge-left.png) |
| Bottom edge | ![The notch on the bottom edge](docs/screenshots/edge-bottom.png) |
| Small | ![Small size](docs/screenshots/size-small.png) |
| Extra large | ![Extra large size](docs/screenshots/size-xlarge.png) |

## Settings

Right-click the tray icon and choose **Settings**, or run the exe again while it's running.

![The Notch page of the settings](docs/screenshots/settings-notch.png)

## Privacy

There's no account, server, or telemetry. Quick voice sends your questions to Claude through your
own Claude Code. Voices and speech models are only downloaded if you choose them, and ElevenLabs is
only used if you add your own key. The [guide](docs/guide.md#privacy) has the details.

## Uninstall

Use **Settings → Uninstall…**, or run `ClaudeNotch.exe --uninstall`. Only Claude Notch's own hooks
are removed from `~/.claude/settings.json`.

## More

- [The full guide](docs/guide.md): every option, shortcut and voice, and how it works.
- [Contributing](CONTRIBUTING.md): building, running the tests, and pull requests.
- Released under the [MIT License](LICENSE).
