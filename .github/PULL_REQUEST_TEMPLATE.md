## What & why

Describe what this PR changes and the motivation.

Fixes: #<!-- issue number, if any -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Documentation
- [ ] Refactor / chore

## Checklist

- [ ] Code, comments, and docs are in English.
- [ ] The server stays fully closed — no change introduces an inbound port.
- [ ] `pinqops deploy` still does not check out or run repository content on the
      server, and the workflow still triggers only on `push: master`.
- [ ] Command arguments are built as discrete list items (no shell strings).
- [ ] `dotnet build -c Release` is warning-free.
- [ ] `dotnet test -c Release` passes (added/updated tests for behavior changes).
- [ ] Docs updated if behavior or configuration changed.
- [ ] No secrets included in code, tests, or logs.

## Notes for reviewers

Anything specific you'd like feedback on.
