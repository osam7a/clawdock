# Contributing to Claude Notch

Thanks for helping out. Bug reports, fixes, and ideas are all welcome.

## Reporting a bug

Open an issue with:

- What you did, what you expected, and what happened.
- Your Windows version, and whether it was a session in the Claude app or a terminal.
- The relevant lines of `%LOCALAPPDATA%\ClaudeNotch\ClaudeNotch.log`. Check them for anything
  private first, such as project names or paths.

For a security problem, please don't open a public issue. Use
[a private report](https://github.com/osam7a/claude-notch/security/advisories/new) instead.

## Setting up

You need Windows 10 or 11, and nothing else: the build uses the C# compiler that ships with Windows.

```powershell
git clone https://github.com/osam7a/claude-notch.git
cd claude-notch
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

That produces `bin\ClaudeNotch.exe`.

## Running your build

| Command | Use it for |
| --- | --- |
| `bin\ClaudeNotch.exe --here` | Running your build without installing it. Only one copy runs at a time, so quit the installed one first (tray icon → Quit). |
| `bin\ClaudeNotch.exe --demo` | Going through the notch's states without Claude. `--demo quick` and `--demo cards` show parts of it. |
| `install.ps1` | Building and installing, replacing the installed copy. |

Claude Code reads hooks when a session starts, so test hook changes in a new session.

## Code guidelines

- **C# 5 only.** The Windows compiler stops there, so no `$"..."`, `?.`, `nameof`, or expression
  bodies. The build tells you if you slip.
- **No dependencies.** The project is one exe, with no NuGet packages and no project file. New files
  anywhere in `src/` are picked up by the build. If you add a framework reference, add it to `build.ps1`.
- **The UI is built in code.** There's no XAML. Colors and fonts are in `src/Notch/Theme.cs`.
- **Keep PowerShell scripts ASCII.** Windows PowerShell 5.1 misreads other characters in them.
- **Follow the code around you.** Short comments that say why, and small focused methods.
- **Never log secrets.** That covers API keys and the contents of chats.
- **Be careful with the user's files.** `~/.claude/settings.json` belongs to the user, so only touch
  Claude Notch's own hooks. Anything downloaded has to be checked against a pinned SHA-256.

The file table in the [guide](docs/guide.md#how-it-works) shows where things are.

## Testing

Run the tests, which also run on every push:

```powershell
.uild.ps1 -Test
.uild.ps1 -Test -Only Json,Settings   # only tests with these words in their names
```

A test is a public static method in a `*Tests` class in `tests`. Tests must not touch your real
`~/.claude`, settings file or Claude app.

They cover the logic, not the screen, so also try your change by hand and say in your pull request
what you tried. Depending on what you changed, check:

- The notch in each style (Classic, Compact, Line, Dot), and on another edge or size.
- A session in the Claude app and one in a terminal.
- A permission request: allow, deny, Esc, and going back to the chat.
- Quick voice, by speaking and by double-pressing to type.
- That your machine's `~/.claude/settings.json` is still intact after installing and uninstalling.

## Commits

Keep commits small, one change each, in the form `type(scope): message`:

```
feat(quick-voice): open a chat in the Claude app
fix(notch): keep large sizes on the screen
docs(readme): explain the mascot's moods
```

Types are `feat`, `fix`, `docs`, `refactor`, `chore`, and `build`. The scope is the area you
touched, such as `notch`, `mascot`, `quick-voice`, `hooks`, or `settings`. Each commit should build.

## Pull requests

1. Branch from `main`.
2. Make the change, add it to `CHANGELOG.md` under Unreleased, and update the README or guide if
   behavior changes.
3. Open the pull request with what changed, why, and how you tested it. Add a screenshot for visual
   changes.

## Releases

1. Bump the version in `src/App/AssemblyInfo.cs`.
2. Turn Unreleased in `CHANGELOG.md` into `## X.Y.Z - YYYY-MM-DD`, and add a new Unreleased above it.
3. Commit, then tag and push it: `git tag vX.Y.Z` and `git push origin main vX.Y.Z`.

GitHub Actions then runs the tests, builds the exe, and publishes the release with its checksum. It
stops without publishing if the tag doesn't match the version or the changelog has no section for it.

## Screenshots

`.\build.ps1 -Screenshots` regenerates `docs/screenshots` with sample data. It covers your screen for
about a minute and restarts Claude Notch afterwards. Add new shots in `tools/Screenshots.cs`.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
