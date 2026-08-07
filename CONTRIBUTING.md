# Contributing

Thanks for your interest in improving pinqops! The core deploy path
deliberately stays small — keep changes there minimal and the security posture
intact (see [Ground rules](#ground-rules)). The dashboard has grown well past
the original single-purpose scope, so "the README says this is out of scope" is
no longer a reason not to propose something; say what problem it solves.

## Ways to contribute

- **Open an issue** — report a bug or request a feature (see below).
- **Open a pull request** — fix a bug, add a feature, improve docs.
- **Improve documentation** — the docs are as important as the code here.

## Opening an issue

Go to the repository's **Issues → New issue** and pick a template:

- **Bug report** — for something that doesn't work as documented. Include your
  `pinqops version`, the relevant Actions/`pinqops` logs, and steps to reproduce.
- **Feature request** — for a new command, option or dashboard capability.

Blank issues are disabled so the right details are captured up front. For
**security vulnerabilities**, do **not** open a public issue — follow
[`SECURITY.md`](SECURITY.md).

## Ground rules

- **English only** for code, comments, commit messages, and docs.
- Keep the deploy path minimal and the security posture intact: the server must
  remain fully closed (no inbound ports), `pinqops deploy` must not check out or
  run repository content on the server, and the workflow must trigger only on
  `push: master`.
- Command arguments must always be built as discrete list items — never as a
  concatenated shell string.

## Project layout

| Path | What it is |
|---|---|
| `src/PinqOps.Core` | Deploy engine + runner installer (library, unit-tested) |
| `src/PinqOps.Cli` | The `pinqops` console app (`deploy`, `install-runner`) |
| `tests/PinqOps.Core.Tests` | xUnit tests |
| `.github/workflows/` | `ci.yml` (build+test), `release.yml` (binary) |
| `examples/workflows/deploy.yml` | Deploy pipeline **template** consumers copy into their app repo |
| `deploy/`, `examples/`, `docs/` | Example compose project, example app Dockerfile, docs |

## Development setup

You need the **.NET 10 SDK**. Clone with submodules so the shared standards under
`.pinq-doq` / `.claude` are present:

```bash
git clone --recurse-submodules <repo-url>
# or, in an existing clone:
git submodule update --init --recursive
```

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

> pinqops is developed with Claude Code using the standards in `.claude/rules/`.
> Using Claude is optional; if you do, see
> [`docs/DEVELOPING-WITH-CLAUDE.md`](docs/DEVELOPING-WITH-CLAUDE.md).

Run the CLI locally:

```bash
dotnet run --project src/PinqOps.Cli -- help
dotnet run --project src/PinqOps.Cli -- version
```

Build the self-contained binary the way the release workflow does:

```bash
dotnet publish src/PinqOps.Cli -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o ./publish
./publish/pinqops help
```

## Tests

- The interesting logic lives in `PinqOps.Core` behind `IProcessRunner` and
  `IFileDownloader`, so it is tested without Docker or the network. Please add or
  update tests for any behavior change.
- `dotnet test -c Release` must pass. CI runs it on every pull request.

## Pull requests

1. Fork and create a feature branch (never target `master` directly — it is
   protected).
2. Make a focused change with tests where it makes sense.
3. Ensure `dotnet build -c Release` is warning-free and `dotnet test -c Release`
   passes.
4. Update the docs if behavior or configuration changed, and fill in the PR
   template.

## Commit messages

Use clear, imperative subject lines (e.g. "Add deploy timeout option").
Reference issues where relevant.

## Cutting a release (maintainers)

Releases are automated by [`release.yml`](.github/workflows/release.yml). To
publish a new `pinqops` binary:

1. Update [`CHANGELOG.md`](CHANGELOG.md).
2. Tag and push:
   ```bash
   git tag v1.2.3
   git push origin v1.2.3
   ```
3. The workflow builds a self-contained linux-x64 `pinqops` binary and attaches
   it to the GitHub Release. Servers install it via the URL in
   [`docs/SETUP.md`](docs/SETUP.md).

## Reporting security issues

Do not open public issues for vulnerabilities — follow
[`SECURITY.md`](SECURITY.md).
