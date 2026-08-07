# Developing with Claude

pinqops is developed with [Claude Code](https://claude.com/claude-code), using
shared engineering standards from **pinq-doq**. Using Claude is entirely
optional — plain `dotnet` works fine — but if you do, this is how the pieces fit
together.

## What's in the repo for Claude

| Path | Purpose |
|---|---|
| `.pinq-doq/` | Git submodule with PinqPonq's shared Claude standards (rules, skills, references). This is the canonical source, committed as a submodule pointer. |
| `.claude/` | **Local and git-ignored.** Generated from pinq-doq by its deliver script; Claude Code auto-loads `.claude/rules/` (`common.md` always; `dotnet-conventions.md` when you touch C#). |

> `.claude/` is not published — it is populated locally so each contributor gets
> the current standards without them being duplicated in the public repo. The
> canonical rules live in [`.pinq-doq/rules/`](../.pinq-doq/rules), and they are
> the same standards a human contributor should follow — see
> [`CONTRIBUTING.md`](../CONTRIBUTING.md).

## Setup

Clone with submodules, then populate `.claude/` from the shared standards:

```bash
git clone --recurse-submodules <repo-url>
# or, in an existing clone:
git submodule update --init --recursive

# Deliver the shared rules/skills into .claude/ (local, git-ignored):
python .pinq-doq/scripts/deliver.py
```

Open the repository in Claude Code (CLI, desktop app, or IDE extension). The
rules load automatically; you don't need to `@import` them. (Using Claude is
optional — you can develop with plain `dotnet` and skip this step.)

## Building a feature with Claude

The codebase has a deliberate shape that Claude follows:

- **`src/PinqOps.Core`** holds the logic behind small interfaces
  (`IProcessRunner`, `IFileDownloader`) so it is unit-testable without Docker or
  the network.
- **`src/PinqOps.Cli`** is a thin argument-parsing layer over Core.
- **`tests/PinqOps.Core.Tests`** covers every behavior, using the fakes in
  `tests/PinqOps.Core.Tests/Fakes/`.

Example — adding a `pinqops status` command:

1. **Describe the change** to Claude, e.g.:
   > "Add a `pinqops status` command that reports whether the app container is
   > running, using the existing `IProcessRunner`. Follow the conventions in
   > `.claude/rules` and add xUnit tests."
2. **Claude implements it** following the pattern:
   - a small class in `PinqOps.Core` (e.g. `StatusReporter`) that takes
     `IProcessRunner`;
   - a `status` branch in `src/PinqOps.Cli/Program.cs`;
   - tests in `tests/PinqOps.Core.Tests` using `FakeProcessRunner`.
3. **It verifies** with `dotnet build -c Release` and `dotnet test -c Release`.
4. **You review the diff** and open a pull request. CI re-runs build + tests.

## Conventions Claude follows (and so should you)

- The standards in `.claude/rules/` — English only, descriptive names, guard
  clauses, no magic values, fail-fast configuration (`common.md`,
  `dotnet-conventions.md`).
- The security posture must stay intact: no inbound port on the server,
  `pinqops deploy` never checks out or runs repo content on the runner, and
  command arguments are built as discrete list items (never shell strings).
- Add or update tests for any behavior change.

## Reviewing AI-authored changes

Claude-authored changes go through the **same** pull-request, CI, and human
review as any other contribution. Review the diff, not the prompt — the diff is
the source of truth.
