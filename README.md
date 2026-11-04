# Clawdock

Run Claude in the background without having to keep checking on it.

Clawdock is a small notch at the edge of your Windows screen. It shows what your Claude sessions are
doing and lets you answer them from wherever you are. You can also press a key and ask Claude
something out loud, without opening a window.

![The notch showing a session at work, with a list of the other sessions](docs/screenshots/sessions.png)

> Clawdock is an independent community project. It isn't made, endorsed, or supported by
> Anthropic. "Claude" is a trademark of Anthropic.

## What you can do with it

- **See your sessions at a glance.** Each session in the Claude app or a terminal shows its progress
  and working time. Hover to list them all, and click one to jump to it.
- **Answer permission requests from the notch.** When a Claude app session wants to run something
  while you're elsewhere, you see the full command and choose Allow or Deny. Terminal sessions still
  ask in the terminal, and the notch tells you and takes you there.
- **Answer Claude's questions.** When an app session ends on a question, its buttons appear on the
  notch, and clicking one presses that button in Claude.
- **Talk or type to Claude from any app.** Hold the shortcut and ask. The answer is read aloud, with
  the key fact shown as a card. Double-press it to type instead.
- **Ask about your sessions.** Ask by voice what one is doing or whether it finished, without
  interrupting it. Sending a session a message always needs your OK.
- **Start a new project by asking.** Claude suggests a name, model and first prompt, and nothing starts
  until you press Allow.

![Quick voice answering a question about the weather, with a card](docs/screenshots/quick-weather.png)

## Get started

**You need** Windows 10 or 11, plus the [Claude desktop app](https://claude.ai/download) or
[Claude Code](https://claude.com/claude-code). Talking and typing to Claude need Claude Code, signed in.

1. Download `Clawdock.exe` from the
   [latest release](https://github.com/osam7a/clawdock/releases/latest). There's nothing else to
   install.
2. Run it. The exe isn't code-signed yet, so Windows SmartScreen may warn you. Choose **More info**,
   then **Run anyway**, or [check the download first](SECURITY.md#checking-a-download).
3. The settings open. If your keyboard has no Copilot key, go to **Shortcut** and pick your own, because
   the Copilot key is the default.
4. Restart any Claude sessions that are already open, so they start reporting to the notch.

To try it, hold your shortcut and ask a question, or start a Claude session and move to another window.

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
- **Downloads about 160 MB on first run,** for speech recognition and the voice, which then run on your
  PC. Until they finish, Windows' own speech and voices are used.
- **Updates itself from this repo's releases.** You can turn this off in **Settings → General**.

There's no account, server, or telemetry. [SECURITY.md](SECURITY.md) lists everything it connects to
and how to verify a release.

## Uninstall

Use **Settings → Uninstall…**, or run `Clawdock.exe --uninstall`. This removes the app, its downloads,
its startup entry and its hooks. Your other hooks stay as they are.

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
- [MIT License](LICENSE).
