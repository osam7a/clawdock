# Security

## Reporting a problem

Please report security problems privately, using **Report a vulnerability** on the repo's
[Security tab](https://github.com/osam7a/clawdock/security), not in a public issue. Only the latest
release gets fixes.

## Checking a download

Each release is built and published by GitHub Actions from its tag, and its files can't be changed
afterwards. To confirm that the exe you downloaded came from that build (from 0.5.0 on, with the
[GitHub CLI](https://cli.github.com)):

```powershell
gh attestation verify Clawdock.exe --repo osam7a/clawdock
```

To compare its checksum with the one in the release notes:

```powershell
Get-FileHash Clawdock.exe -Algorithm SHA256
```

You can also skip the download and build it yourself. It needs no SDK or packages, just the compiler
that comes with Windows, so all the code that runs is in this repo. See the
[README](README.md#install). Your build's checksum won't match the release's, because the compiler
stamps each build.

The exe isn't code-signed yet, so Windows SmartScreen may warn about it. The steps above are how to
check it in the meantime.

## What it does on your PC

It runs as you and never asks for administrator rights. It:

- Copies itself to `%LOCALAPPDATA%\Clawdock` and adds a startup entry and a Start menu entry, for your
  user only.
- Adds its hooks to `~/.claude/settings.json`, after keeping a backup of that file. It only ever
  changes its own hooks.
- Uses a keyboard hook to catch the Copilot key, and reads and presses controls in the Claude app.
- Uses the microphone while quick voice is listening.

**Settings → Uninstall** removes all of this.

## What it connects to

There's no account, server, or telemetry.

| Where | When |
| --- | --- |
| `api.github.com`, `github.com` | Checking for and downloading updates, every 6 hours, unless you turn it off |
| `github.com`, `huggingface.co` | Downloading the speech model and natural voice on first run |
| Anthropic | Through your own Claude Code, when you use quick voice |
| `api.elevenlabs.io` | Only if you add your own key and choose that voice |
| Microsoft | Only if you pick the Windows speech engine with its online speech turned on |

## How updates are checked

An update only installs if all of these hold:

- It comes from this repo's latest release, over HTTPS.
- Its SHA-256 matches the one GitHub reports for the release file.
- It's a .NET program named Clawdock, at exactly the release's version, and newer than the one running.

The speech and voice downloads are checked against SHA-256 values written into the code.

All of this trusts this GitHub repo. Anyone who could publish a release here could ship code to every
install, so turn off **Update automatically** in the settings if you'd rather update by hand.
