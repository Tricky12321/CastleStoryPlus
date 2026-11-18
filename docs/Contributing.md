# Contributing

## Reporting bugs

This is a fan mod in early development, so bugs will happen. Please report them as [GitHub issues](https://github.com/Tricky12321/CastleStoryPlus/issues) and include:
- your system (Windows or Linux) and the mod version;
- what you did and what happened;
- the log file `BepInEx/LogOutput.log` from the Castle Story folder.

Please do not report problems caused by the mod to Sauropod Studio. Turn the mod off first to check whether a problem also happens in the normal game.

## Roadmap

Castle Story Plus will keep growing beyond quality-of-life fixes. Planned next:

- **New maps:** new islands and scenarios to build on and defend.
- **More enemies:** new enemy types and invasion waves that call for different defences.
- **New creatures that work for you:** new units that take on tasks such as hauling, gathering or building, next to the bricktrons.

The detailed list of features planned over time is in [Roadmap](Roadmap.md): taming, housing and training animals, and portals.

These plans can change. Ideas and wishes are welcome as [issues](https://github.com/Tricky12321/CastleStoryPlus/issues).

To build the mod yourself, see [Development](Development.md).

## Pull requests

Every pull request uses the same form ([template](../.github/pull_request_template.md)), which GitHub fills in when
you open one: what happens, the cause, the change, how it was tested, AI use and a checklist.

**AI use must be stated.** Say whether AI was used, which tool and model (for example Claude Code with Claude Opus,
GitHub Copilot, ChatGPT with GPT-5) and what it did (wrote the code, found the cause, wrote the description). AI help
is welcome; the pull request is still yours, so test it in the game and check what it wrote. This applies to pull
requests opened from 31-10-2026 on.

Keep a pull request to one thing, and add a line to `CHANGELOG.md` under `## Unreleased`.

**Logic comes with unit tests.** New decisions and calculations go into a `Logic` folder as pure classes with unit tests
(`dotnet test CastleStoryPlus.UnitTests`, no game needed); patches and components stay thin. See [Testing](Testing.md).
The "Unit tests" check runs them on every pull request.

**The code style is checked.** A pull request can only be merged when the "Code style" check passes. It runs
`tools/check-style.sh`, which you can run yourself before pushing (no game install needed):

- Braces on every block (`if`, `else`, `for`, `foreach`, `while`, `do`, `using`), also for a single line.
- Opening braces on their own line, tabs for indentation, file-scoped namespaces (the rest is in `.editorconfig`).
- 24-hour time (`HH:mm`) and dates that start with the day or year (`dd-MM-yyyy` on screen, `yyyy-MM-dd` in data),
  always with an explicit format, never `ToShortDateString()` or similar.

`tools/check-style.sh --fix` fixes the layout and the braces; the date and time rules are fixed by hand. Rider and
Visual Studio read `.editorconfig`, so the IDE's own formatting follows the same rules.

Everyone whose pull request is merged is listed in [CONTRIBUTORS.md](../CONTRIBUTORS.md); the name is added when the pull
request is merged.

## License

Contributions are licensed under the [GNU General Public License v3](../LICENSE) or later, as the rest of the code,
with the additional permission in [NOTICE](../NOTICE).
