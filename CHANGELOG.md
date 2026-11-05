# Changelog

What changed in each release. New work goes under Unreleased, and that heading becomes the version
when it's released.

## Unreleased

### App

- Settings now shows "Based on Clawdock by Ousama Alhennawi" in the sidebar.

### Project

- The license now adds one condition to MIT: forks and copies must show that same line in their
  settings or about screen.

## 0.5.1 - 2026-11-05

### App

- Your own shortcut now has a **Record** button. Click it and press the keys, and you see them as you
  hold them. Esc cancels.

## 0.5.0 - 2026-11-05

### App

- A new install now starts quick voice from the shortcut, with Whisper's 60 MB model and the natural
  voice Bruno. They download by themselves on first run, about 160 MB. Existing installs keep their
  settings.
- Automatic updates now refuse a release that has no checksum.
- The exe's Properties now show its author and license.

### Project

- Releases can be verified: each one records that GitHub Actions built it from its tag, using
  `gh attestation verify`.
- Workflows pin their actions to exact commits and only get the permissions they need.
- Added `SECURITY.md`, CodeQL scanning and Dependabot for the workflows' actions.

## 0.4.0 - 2026-11-05

### App

- **Claude Notch is now Clawdock.** Running the new exe moves your settings, downloads and quick voice's
  chat over, replaces the hooks and removes the old startup entry. The exe is now `Clawdock.exe`, in
  `%LOCALAPPDATA%Clawdock`. Sessions already open report to the notch again after they restart.
- Links in quick voice's answers are underlined and open when you click them. They aren't read aloud.
- The compact style is wider: as wide as classic when open, and with more room for a session's status.
- The installed copy updates itself from GitHub releases. You can turn this off in the settings.

### Project

- Split the largest source files into smaller ones and grouped `src/` into folders. The app behaves
  the same.
- Releases are now published automatically from a version tag.
- `build.ps1 -Screenshots` regenerates the README's screenshots.
- Added this changelog, and issue and pull request templates.

## 0.3.0 - 2026-11-04

### New

- **Start new projects.** Ask quick voice for a new project. It picks a name, model and effort, and
  writes a first prompt. After you press Allow, it makes the folder and opens a real session in the
  Claude app.
- **Auto mode.** A new access level for quick voice, and new project sessions can start in Auto.
- **Laptop mascot.** The mascot now codes on a laptop while it works.
- **Esc cancels.** Press Esc while talking to quick voice, or while it's working, to cancel.

### Fixed

- New project names like `CON.txt` are now refused, like `CON`.
- A card showing `1%` no longer shows a full bar.

### Project

- Automated tests and CI on every push.
- A shorter README with screenshots. The full reference moved to `docs/guide.md`.

## 0.2.2 - 2026-11-04

### New

- **Search your finished sessions.** Quick voice can answer questions about any Claude Code session
  on your PC, running or finished.
- **Permission request details.** Click a request to see the whole command, the edit it wants to
  make, and the folder.

### Changed

- Clicking a permission request used to open Claude. To go to Claude now, right-click and choose
  **Open Claude**.

## 0.2.1 - 2026-11-04

### Changed

- **Wrenlet is Claude Notch again.** The name, mascot and colors are back. Running `ClaudeNotch.exe`
  moves your settings and downloads back, and replaces the hooks.

## 0.2.0 - 2026-11-04

### New

- **Ask about your other sessions.** Quick voice can answer questions about your running sessions
  from a snapshot of their transcripts, without interrupting them. Messaging a session needs your
  OK on the notch.

## 0.1.1 - 2026-11-04

### Changed

- Renamed to Wrenlet, with its own mascot and colors.

## 0.1.0 - 2026-11-04

The first release: live sessions, permission requests, Claude's questions, quick voice, quick typing,
opening a chat in Claude, mascot moods, and four styles.
