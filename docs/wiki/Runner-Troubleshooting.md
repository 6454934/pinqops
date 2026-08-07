# Runner Troubleshooting

## The repo shows "There are no runners configured"

The classic cause: the server has a runner **registered to a different
repository** (a leftover from an earlier setup). Older versions treated any
`/opt/actions-runner/.runner` file as "installed" and just started whatever
`actions.runner.*` systemd unit existed — reporting success while the newly
selected repository stayed runner-less on GitHub.

pinqops now handles this correctly, on both paths:

- The readiness check compares the `.runner` registration URL with the
  selected repository; a mismatch counts as **not installed**.
- The install (wizard or `pinqops setup`) stops and uninstalls the old
  service, de-registers the old runner (`config.sh remove` with a removal
  token minted for the *old* repo — best effort; local files are
  force-cleaned if no token is available), then registers to the selected
  repository.
- The systemd unit is resolved from the runner directory's own `.service`
  file — never "the first `actions.runner.*` unit".
- The wizard's last step asks GitHub whether the runner actually appeared.

If you had a stale registration, the old repository may briefly list an
offline runner; GitHub purges runners that stay offline.

## Runner installed but offline

- Check the service:
  `systemctl status "$(cat /opt/actions-runner/.service)"`
- Start it from the UI (Runner row → **Start service**) or:
  `cd /opt/actions-runner && sudo ./svc.sh start`
- Missing native deps on minimal images:
  `sudo /opt/actions-runner/bin/installdependencies.sh`

## Runner online but jobs never run

- The deploy workflow must target your runner's labels:
  `runs-on: [self-hosted, pinqops-prod]`.
- The workflow only triggers on push to your default branch — merge a PR to test.

## Token errors while listing runners

Listing runners needs repo-admin (fine-grained PAT: *Administration: read*).
Without it, the dashboard degrades that row to "online state unknown" instead
of failing — deploys still work.
