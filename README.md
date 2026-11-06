# Clawdock

[![Latest release](https://img.shields.io/github/v/release/osam7a/clawdock)](https://github.com/osam7a/clawdock/releases/latest)
[![Build](https://img.shields.io/github/actions/workflow/status/osam7a/clawdock/ci.yml?branch=main&label=build)](https://github.com/osam7a/clawdock/actions/workflows/ci.yml)
[![Downloads](https://img.shields.io/github/downloads/osam7a/clawdock/total)](https://github.com/osam7a/clawdock/releases)
[![License: custom, MIT terms plus attribution](https://img.shields.io/badge/license-custom%3A%20MIT%20%2B%20attribution-blue)](LICENSE)
![Windows 10 and 11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)
[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-support-FFDD00?logo=buymeacoffee&logoColor=000000)](https://buymeacoffee.com/ousama)

**Press the Copilot key and talk to Claude, from any app.**

Clawdock turns your keyboard's Copilot key into a Claude key for Windows. Hold it, ask out loud, and let
go. The answer is read aloud and appears in a small notch at the edge of your screen, without opening a
window. No Copilot key? [Use any shortcut you like](#no-copilot-key-use-your-own-shortcut).

![Quick voice answering a question about the weather, with a card](docs/screenshots/quick-weather.png)

> Clawdock is an independent community project. It isn't made, endorsed, or supported by
> Anthropic. "Claude" is a trademark of Anthropic.

## How the key works

| Do this | What happens |
| --- | --- |
| **Hold** the key, speak, let go | Your question goes to Claude and the answer is read aloud |
| **Press** the key once, then speak | Same thing, without holding it. It sends when you stop talking |
| **Press** it during an answer | Cuts the answer off so you can ask something else |
| **Double-press** it | Opens a small box to type in instead |
| Press **Esc** | Cancels |

## No Copilot key? Use your own shortcut

If your keyboard doesn't have a Copilot key, it takes about 30 seconds to pick another shortcut:

1. Right-click the Clawdock icon in the system tray and choose **Settings**. They also open by
   themselves the first time you run it.
2. Click **Shortcut** on the left.
3. Set **Start talking to Claude with** to **My own shortcut**.
4. Click **Record** and press the keys you want. It starts as `Ctrl+Shift+Space`, so if that suits
   you, skip this step.
5. Click **Save**.

Your shortcut works right away, and everything in the table above works the same with it.

- **Which keys work:** use Ctrl, Alt, Shift or Win plus another key, or a function key such as `F9` by
  itself.
- **If you see a message that it's already used by another app,** pick a different combination.
- **Avoid `Ctrl+Alt+Space`,** because that's the Claude app's own shortcut.

## What else it does

The notch also keeps an eye on your Claude sessions, so you can leave them running and get on with
something else.

![The notch showing a session at work, with a list of the other sessions](docs/screenshots/sessions.png)

- **See your sessions at a glance.** Each session in the Claude app or a terminal shows its progress
  and working time. Hover to list them all, and click one to jump to it.
- **Answer permission requests from the notch.** When a Claude app session wants to run something
  while you're elsewhere, you see the full command and choose Allow or Deny. Terminal sessions still
  ask in the terminal, and the notch tells you and takes you there.
- **Answer Claude's questions.** When an app session ends on a question, its buttons appear on the
  notch, and clicking one presses that button in Claude.
- **Ask about your sessions.** Ask by voice what one is doing or whether it finished, without
  interrupting it. Sending a session a message always needs your OK.
- **Show Claude your screen.** Turn this on in the settings and ask "what's the answer to this question?".
  Claude takes a screenshot of the screen you're on only when your question needs one.
- **Start a new project by asking.** Claude suggests a name, model and first prompt, and nothing starts
  until you press Allow.

## Get started

**You need** Windows 10 or 11 and [Claude Code](https://claude.com/claude-code), signed in. Talking and
typing to Claude run through it. For just the session features, the
[Claude desktop app](https://claude.ai/download) is enough.

1. Download `Clawdock.exe` from the
   [latest release](https://github.com/osam7a/clawdock/releases/latest). There's nothing else to
   install.
2. Run it. The exe isn't code-signed yet, so Windows SmartScreen may warn you. Choose **More info**,
   then **Run anyway**, or [check the download first](SECURITY.md#checking-a-download).
3. The settings open. If your keyboard has no Copilot key,
   [choose your own shortcut](#no-copilot-key-use-your-own-shortcut) now.
4. Restart any Claude sessions that are already open, so they start reporting to the notch.

To try it, hold the Copilot key or your shortcut and ask, "What's the capital of Japan?"

## Good to know

**The notch isn't always there.** By default it shows when something's going on, and hides while
you're looking at Claude or using a fullscreen app. You can change this in **Settings → When it
shows**. If you can't find it, look for the icon in the system tray.

**Open the settings** by right-clicking the tray icon, or by running the exe again.

**Quick voice uses your own Claude.** It runs through your Claude Code, so it counts toward your usage.
Tools that only look things up run without asking, and anything that acts asks you on the notch first.

**Make it yours.** There are four styles (classic, compact, a thin line and a dot), four sizes, and any
screen edge on any display. You can also pick other voices and speech engines, including ElevenLabs
with your own key.

## What it does to your PC

- **Installs for your user only.** It copies itself to `%LOCALAPPDATA%\Clawdock` and starts with Windows.
  It never asks for administrator rights.
- **Adds hooks to `~/.claude/settings.json`,** which is how sessions report to it. It backs the file up
  first, and only ever changes its own hooks.
- **Adds a `voice-replies` skill to `~/.claude/skills`,** which sets how quick voice answers. You can edit it.
- **Downloads about 160 MB on first run,** for speech recognition and the voice, which then run on your
  PC. Until they finish, Windows' own speech and voices are used.
- **Updates itself from this repo's releases.** You can turn this off in **Settings → General**.

There's no account, server, or telemetry. [SECURITY.md](SECURITY.md) lists everything it connects to
and how to verify a release.

## Uninstall

Use **Settings → Uninstall…**, or run `Clawdock.exe --uninstall`. This removes the app, its downloads,
its startup entry, its hooks and the backup of your settings file. The `voice-replies` skill goes too, unless you've edited it.
Your other hooks stay as they are. If you used the VOICE_CHATS history, that chat stays in your Claude Code history.

## Build it yourself

You don't need an SDK or any packages, because it builds with the compiler that comes with Windows.

```powershell
git clone https://github.com/osam7a/clawdock
cd clawdock
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

## Learn more

- [The guide](docs/guide.md): every feature, setting and voice, and how it works.
- [Contributing](CONTRIBUTING.md): building, testing and pull requests.
- [Changelog](CHANGELOG.md): what changed in each release.
- [License](LICENSE): a custom license, MIT's terms plus one condition, so it isn't standard MIT and license tools won't detect it as MIT. Forks and copies must show "Based on Clawdock by Ousama Alhennawi" in their settings or about screen.

If Clawdock saves you time, you can [buy me a coffee](https://buymeacoffee.com/ousama).
