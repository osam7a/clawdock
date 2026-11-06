# Changelog

What changed in each release. New work goes under Unreleased, and that heading becomes the version
when it's released.

## Unreleased

## 0.6.0 - 2026-11-06

### App

- The shortcut now has three choices: **Quick Voice**, **Quick Prompt** (a new one that opens the text box on the
  notch) and **Claude Voice**. If you chose Claude's quick entry, you move to Quick Prompt, and "Claude voice, or
  type without a mic" becomes Claude Voice, which still opens quick entry when there's no mic.
- **Claude can see your screen when it needs to.** Turn on **Let Claude see my screen when it needs to** and
  ask something like "what's the answer to this question?". Claude asks for a screenshot of the screen you're
  on only when the question needs one, and the notch says so. With it off, Clawdock takes none and Claude is told not to look.
- In Auto mode, shell commands and tools that can see your screen or camera now wait for **Allow** on the notch. Before,
  Claude Code's safety checks could let a screenshot command through without asking.
- Every screenshot Clawdock takes, and what you allow or decline for quick voice, is written to the log.
- Noise picked up while listening for your reply no longer closes the answer after a few seconds.
- Answers on the notch now show formatting: bold, italic, strikethrough, inline code, lists, headings, quotes, code blocks
  and tables. None of the marks are read aloud.
- × on a permission request or a question now closes only that, instead of hiding the notch until you went back to Claude.
- A finished answer now shrinks to the small bar instead of the notch disappearing. Click the bar to bring it back.
- Once Claude has read a file, a session or your screen in a conversation, each web page it wants to fetch waits for
  **Allow** on the notch, so a web page can't quietly talk it into sending that data somewhere. Files that usually hold
  keys always ask, and connector tools named for both looking up and acting, like `get_or_create`, now ask too.
- Keys in their usual shapes are hidden from the session summaries quick voice reads.
- Hooks now check that the notch's pipe belongs to you, so another user on the PC can't answer permission requests.
- Uninstalling now also removes the settings backup and the `voice-replies` skill, unless you've edited the skill.
- Moving an old install now checks the folder really is one, rather than going by its name.
- An update that doesn't start and stay up is undone, and isn't tried again unless you ask.
- With the taskbar set to auto-hide, a maximized window no longer counts as fullscreen, which was hiding the notch.
- Crashes on background threads and settings that can't be saved are now written to the log.
- New projects on Haiku fall back to the `haiku` alias if the specific model isn't available.

### Project

- The README now describes the license as custom rather than MIT.
- Fixed the test commands in `CONTRIBUTING.md`.

## 0.5.2 - 2026-11-05

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
  `%LOCALAPPDATA%\Clawdock`. Sessions already open report to the notch again after they restart.
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
