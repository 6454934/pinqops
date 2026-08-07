# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to a rolling release model (latest `master` only).

## [Unreleased]

### Fixed

- **Caddyfile updates are visible inside the proxy container again.** The live
  Caddyfile is bind-mounted as a single read-only file; atomic rename replaced
  its inode on the host, so the host path showed the new site block while
  `caddy reload` kept reading the previous (often site-less) bytes — TLS then
  failed with unexpected EOF on :443. Writes now update that file in place so
  the mount keeps seeing the current content.
- **WaitingDns no longer reloads a site-less Caddyfile that kills the proxy.**
  While every new Cloudflare domain is deferred, the render has no site blocks
  (global options only). Reloading that still tears down every listener — the
  same outage as adapting a header-only file to `{}` — and left Caddy running
  empty config even after the domain was later released onto disk. Apply now
  refuses a site-less write whenever `domains.json` still retains enabled
  domains (deferred count).
- **The wait for DNS no longer waits on a cache entry it created itself.** Adding
  a domain looks the name up before the record exists, so the box's resolver
  caches NXDOMAIN for the zone's SOA minimum — five minutes on Cloudflare. The
  record was written a second later and was live at the authority immediately,
  but every poll of the ninety-second wait re-read that cache entry, so a
  brand-new name almost always timed out with “DNS was written but has not
  propagated yet” and the domain never reached the proxy. Provisioning now falls
  back to a public resolver (Cloudflare, then Google) when the local one has
  nothing, which sees the record as soon as it is written.

## [1.0.12] - 2026-08-08

### Fixed

- **A domain whose DNS propagated slowly no longer stays dead.** New domains are
  held out of the Caddyfile (`ProxyDeferred`) until provisioning reaches Apply,
  but the DNS wait gives up after ninety seconds and returned before that step —
  so a slow record left the domain absent from the proxy for good: no site block,
  no certificate, no HTTP, and no sign of it in the domain list. The proxy now
  retries provisioning by itself every five minutes until it succeeds, so DNS
  arriving late is enough; pressing Point here is no longer the only way back.
  The route still stays out of the Caddyfile until DNS genuinely resolves, so no
  ACME attempt is spent on a name that is still NXDOMAIN.
- **The domain list says when a domain is still waiting for DNS.** Such a row
  used to read as healthy while the address answered nothing.
- **The dashboard page cannot be served from a stale cache.** `/` now carries an
  ETag over its own bytes and `Cache-Control: no-cache`, so an updated binary
  shows its UI on the next load instead of a cached copy of the old one.

### Changed

- **Domains proxy settings stay collapsed once saved.** ACME e-mail, DNS
  provider, and edge mode show a one-line summary after they are set; the edit
  form only opens when you expand it (ACME still opens once if e-mail is empty).
  Reloading the page no longer forces the “Save ACME settings” panel open.
- **Master releases itself.** Once CI passes on master, the next patch release is
  cut automatically (debounced, so a burst of commits produces one release), which
  is what `pinqops-ui update` installs. Releasing by hand still works.

## [1.0.11] - 2026-08-07

### Fixed

- **Cloudflare domain add no longer starts ACME during DNS wait.** New domains
  are stored with `ProxyDeferred` until HTTPS provision releases them; foreign
  Apply calls cannot emit the site block while public DNS is still NXDOMAIN.
- **Empty Caddyfile no longer wipes a running proxy.** A header-only render
  (Caddy adapts it to `{}`) is refused when `domains.json` still has emittable
  routes — even without last-good. Last-good is restored when present.
  Intentional “remove the last route” still applies. Caddyfile/last-good writes
  are atomic; Apply is serialized; `domains.json` Load retries on empty/corrupt
  concurrent reads.
- **Wildcard Proxied flip waits for the certificate.** Apply (DNS-01) → WaitCert
  → Point Proxied, so EnsureEdge reload no longer cancels ACME mid-challenge.
- **Delete cancels in-flight HTTPS provision** so a background job cannot flip
  Proxied after the domain is gone.
- **TLS probe requires a covering, non-expired certificate** (SAN/CN match)
  before Proxied shortcuts and WaitCert succeed.
- **Soft-fail provision jobs report `phase=error` and `ok=false`** when DNS/cert
  timed out; job prune never drops in-flight work.

## [1.0.10] - 2026-08-07

### Added

- **Live stage progress when adding a domain (or Point here) with Cloudflare.**
  HTTPS setup runs as a background job; the UI polls and shows writing DNS,
  waiting for DNS, applying the proxy / ACME, waiting for the certificate, then
  enabling Proxied — instead of a frozen “Working…” button for minutes.

## [1.0.9] - 2026-08-07

### Fixed

- **Cloudflare domain add no longer races Let's Encrypt.** When a DNS provider is
  configured, pinqops writes a temporary DNS-only A record, waits until public DNS
  matches this server, applies Caddy (HTTP-01), waits for a live certificate on
  localhost:443, then flips the record to Proxied. Operators do not grey-cloud by
  hand; the end state is orange cloud with an origin cert that works under SSL
  Full. Point here (Proxied) uses the same path when the cert is not ready yet.
  Wildcards still use DNS-01 and Point Proxied after Apply.

## [1.0.8] - 2026-08-07

### Added

- **Optional Cloudflare Zone ID and Account ID** on the DNS provider settings.
  Zone ID skips the `GET /zones?name=…` walk (helps when that call times out).
  Account ID narrows zone search for multi-account tokens. Neither replaces
  **Zone → DNS → Edit** (and Zone Read) on the API token.

### Fixed

- **Cloudflare authentication error (code 10000)** now tells operators to use a
  Custom API Token with Edit zone DNS — not a Global API Key as Bearer — and
  that Zone ID alone does not grant DNS write. Soft-fail toasts point the same way.

## [1.0.7] - 2026-08-07

### Added

- **Domain add auto-points Cloudflare** when a DNS provider secret is configured.
  The Caddy route is kept even if the API write fails; the toast explains and
  Point here retries. Proxied (orange cloud) by default.

- **Operator-facing Information logs** for PinqOps in `journalctl -u pinqops-ui`.
  Domain add, Cloudflare zone/record steps, publish EXPOSE overrides and purge
  stages are visible without raising Microsoft framework noise.

### Changed

- **Log collection settings use a multi-select** of containers instead of a
  comma-separated text field. Archive search defaults to the last 10 minutes.
  Retention days are editable in the form.

- **Cloudflare DNS HttpClient timeout is 60s** (was 20s), with timeout errors
  naming the API path that stalled.

### Fixed

- **Publish form syncs the container port** after create-compose when EXPOSE
  overrides a stale request. Editing `.env` cannot re-introduce a conflicting
  `PINQOPS_CONTAINER_PORT` when the Dockerfile EXPOSE is known.

## [1.0.6] - 2026-08-07

### Fixed

- **Cloudflare "Point here" no longer 405s when an A record already exists.**
  Find stores record ids as `zone/record`; PUT incorrectly pasted that composite
  into the path (`…/dns_records/{zone}/{id}`), which Cloudflare answers with
  `method_not_allowed` (code 1001). The update path now uses only the record id,
  matching delete.

- **App remove tears down the real compose project.** Purge runs
  `compose -p <project> down -v`, falls back when the YAML is already gone, and
  force-removes any remaining containers labelled with that project — so a
  directory-derived name cannot leave `repo-app-1` running after the app folder
  is deleted. Missing per-app networks are ignored (idempotent), not surfaced as
  warnings.

- **Create-compose prefers Dockerfile EXPOSE over a conflicting request.** The
  publish UI already seeded EXPOSE; the server still accepted a stale
  `PINQOPS_CONTAINER_PORT` (e.g. 8085 with `EXPOSE 80`) and published
  `8085→8085` while the app listened on 80 (`Connection reset` /
  `ERR_CONNECTION_REFUSED`).

## [1.0.5] - 2026-08-07

### Changed

- **Removing an app purges its infrastructure.** Containers, volumes, compose
  project, runner, proxy routes, the per-app Docker network, app-scoped secrets
  and grants are torn down before the dashboard row is dropped — re-adding the
  same repo starts clean. The remove dialog stays busy until purge finishes.

- **GitHub repo select waits for Publish.** Choosing a repository opens the
  publish panel only; connection create, runner start and deploy run when you
  press Publish.

### Fixed

- **Publish prefers the Dockerfile EXPOSE for the container port** over a stale
  `.env` value. A leftover `PINQOPS_CONTAINER_PORT=8085` with `EXPOSE 80` used to
  publish `8085→8085` while the app listened on 80 (`ERR_CONNECTION_REFUSED`).

- **Cloudflare DNS errors surface as 502 with the provider message** instead of
  a opaque 500 correlation id. Timeouts are wrapped the same way as connect
  failures, and empty Cloudflare error bodies include the HTTP status instead of
  "no reason given".

- **GitHub repo select no longer abandons the publish panel.** Choosing a
  repository while the GitHub view was still loading could clear the wizard,
  jump to the app page, or look like a silent auto-publish when a removed app's
  compose/runner was still on disk. The panel stays open until Publish; reattach
  is explained instead of implied.

- **Catalog install warns when ports bind to loopback only**, and the publish
  live card notes firewall when a host port may be unreachable from outside.

## [1.0.4] - 2026-08-07

### Added

- **Offsite backup credentials on the Backups page.** Object storage buckets share
  one credential set with offsite copies; the form that saves them was missing, so
  bucket create answered 400 and the empty state looked like a missing menu. The
  Buckets page now banners and links to Backups when storage is not configured.

- **Custom certificates on a domain, and a live TLS probe.** Domain settings now
  show whether the proxy is answering HTTPS for that name — subject, issuer and
  expiry, or the handshake error. Beside Let's Encrypt you can create a CSR, keep
  the private key on the server, paste the signed full chain back, or switch the
  domain back to automatic ACME. Custom files live under the proxy directory
  (already mounted into Caddy), so no container recreate is required.

- **Proxied Cloudflare records by default.** "Point here" asks whether to use the
  orange cloud; proxied is checked. DNS-only remains available. A proxied write
  turns edge mode on when it is off, and the DNS check accepts Cloudflare
  addresses when edge mode is trusting them.

- **What the Let's Encrypt e-mail actually does.** The Domains install panel says
  the steps — proxy, domain, DNS, then Caddy obtains the cert — and the e-mail can
  be edited after install without wiping the staging choice when saving a DNS
  provider.

- **Secrets.** A new **Secrets** page holds the values your apps need but your
  repository must not — API keys, database URLs, signing keys. They are stored
  encrypted (AES-256-GCM, the same `SecretBox` that already protects the GitHub
  token), and pinqops writes them into each app's compose `.env`, so the next
  deploy carries them and **nothing on the runner has to fetch anything**. The
  store is the source of truth; `.env` is derived from it and rewritten whenever
  it changes.

  A secret applies to every app or to one of them, and an app-scoped secret
  shadows a global one of the same name — two secrets can never fight over the
  same `.env` key. Withdrawing one actually withdraws it: deleting a secret, or
  narrowing it to a single app, clears the value out of the `.env` of every app
  that no longer has it, rather than leaving a retired credential live in a
  running container. Variables you wrote yourself are untouched, as are the
  deploy-pinned image and tag — names beginning with `PINQOPS_` are refused
  outright, because a secret called `PINQOPS_IMAGE` would point your app at
  something else on the next deploy.

  Every value is versioned. **Rotate** replaces it — with a value you supply or
  a generated one — and keeps the old version, so **History** can roll back to
  it without discarding what came after; rolling forward again is the same
  operation. The last ten versions are kept, and whatever is current is always
  still on file.

  The whole family is admin-only, reads included: the names alone say which
  credentials this server holds and which app each belongs to. That also means
  every read is written to the audit log, so a reveal is a matter of record
  rather than a special case. A secret that saved but could not reach some app's
  `.env` says so, naming the apps — the value is stored, and those apps are still
  serving the previous one until the permissions are fixed.

- **Listings show what you are meant to see.** The gate above guards one resource
  at a time, named by the route; a listing returns a set, so the containers, the
  servers, the apps, the backup targets and the domains each filter their own
  rows.

  The rule is deliberately more permissive than the one for actions, and it is
  what makes teams safe to turn on: **a resource nobody has claimed stays visible
  to everyone who could already see it.** Only once a team holds a grant on
  something does it disappear for people outside that team. So an install with no
  grants looks exactly like an install with no teams, and scoping something is a
  deliberate act with a visible effect rather than a silent narrowing.

  That asymmetry is not new — listing every container has always been open to any
  signed-in user while managing one has always been restricted to its owner. What
  changes is only that a claimed resource can now be hidden from people it was
  not claimed for.

  A domain is not claimed in its own right: it belongs to whatever it points at,
  so it is visible exactly when its target is. One fewer thing to keep in step,
  and a route cannot end up listed for someone who cannot see the app it reaches.
  An admin is never filtered, including rows too malformed to identify — which is
  what keeps a mis-grant repairable.

- **Grants now decide access.** Authorization runs in two stages, and they answer
  different questions. The role — viewer, deployer, admin — decides whether this
  *kind* of caller may ever perform this *kind* of action, exactly as before and
  unchanged. What follows decides whether they may act on *this particular*
  resource: an admin always, the personal owner of a container, or a member of a
  team the resource has been granted to. Nothing in the second stage can widen
  what the first refused, so a viewer in a team with full access still cannot
  deploy.

  Personal ownership and team grants are unioned rather than one replacing the
  other. "This container is mine" is what a solo operator actually has, and it
  keeps working with no teams anywhere; a team grant is an additional way in, not
  a migration target. Everything that worked before works the same, and a
  deployer still cannot reach a container nobody granted it — a resource with no
  owner and no grant is admin-only, which is what keeps every system container on
  the host protected.

  A grant names the host as well as the resource, so one on this server never
  opens a same-named container on another. Revoking takes effect immediately, in
  the same process, with no restart.

  Under the hood the container-ownership gate became the general one — the eleven
  routes it governs read the same, and each now declares what it acts on and at
  what level, so a typo is a startup failure rather than a route that quietly
  governs nothing.

- **Invite people by email instead of handing out passwords.** An admin sends a
  link; whoever follows it picks their own username and password and lands signed
  in. The role and the team are decided when the invitation is sent — nothing in
  the body that accepts it can change either.

  **The point is the password that never gets typed by two people.** The
  alternative — creating the account and telling somebody the password — means it
  travels through a chat window and is known to two people from the first day.

  **The link works once.** It is spent under the same lock that checks it, so two
  acceptances arriving together produce one account rather than two; if the second
  loses that race the account it just created is removed again. A name that is
  already taken, or a password that fails the policy, leaves the invitation usable
  so the same person can come back and try again — the failure is theirs to fix,
  not a reason to need a new link. Withdrawing one takes effect immediately, and an
  invitation that has been accepted cannot be withdrawn: that would be saying the
  account should not exist, which is a different operation on a different page.

  **Expired, withdrawn, already used and never existed all answer the same way.**
  Telling them apart tells somebody holding a guess which half of it was right. The
  link is an id and 32 random bytes, and only a hash of the second half is stored —
  the same shape the API tokens use, so a copied-away `invitations.json` contains no
  working link and the listing that renders the page cannot leak one. Sending is
  capped per sender per hour: an invitation endpoint is a way to make this server
  send mail to an address of the caller's choosing, and without a cap it is a way to
  make it send a lot of it.

  If no relay is configured the link is still returned and shown, to pass on by
  hand. An invitation that silently went nowhere would be worse than one that has
  to be copied.

- **Two-factor authentication, with a QR code drawn on the server.** Any
  authenticator app works — it is the standard six-digit code. Turning it on gives
  ten single-use recovery codes, and an admin can ask every account to use one.

  **Enrolment is two steps, and that is the whole safety property.** Starting it
  writes a secret and changes nothing about signing in; it takes effect only once
  a code that secret produced has been typed back, which proves the app really has
  it. A QR that failed to scan, or scanned into the wrong app, therefore fails in
  front of the person setting it up — rather than locking them out of their own
  server, where there is no support desk.

  **A code cannot be used twice.** The step it belonged to is recorded, and any
  step at or below the stored one is refused however correct the arithmetic is.
  Without that, a code stays valid for the rest of its window — so anyone who
  watched it being typed, or read it out of a log, can sign in again within the
  minute. The second step is throttled by the same lockout as the password, on the
  same per-account bucket: six digits is a million combinations, which is not many
  when a machine is doing the typing.

  **The password step no longer hands out a session.** It hands out a challenge:
  minted only by a correct password, gone in five minutes, resolving to a username
  and nothing else. It is not spent by a wrong code — a mistyped digit should not
  mean sending the password again — but it is spent the moment it signs somebody
  in. Requiring two-factor org-wide does not lock anyone out either: an account
  without one still signs in and is asked to finish setting one up. An admin who
  turned it on from their phone and then could not reach the dashboard would have
  no way back.

  The QR is generated here rather than fetched or bundled: the dashboard is one
  HTML file with one inline script under a policy that hashes it, so a CDN script
  tag is the one thing that policy exists to refuse. It is byte mode, level M,
  versions 1–10 — enough for an `otpauth://` URI and nothing more, because every
  unused mode is another table to get subtly wrong. The tables are not taken on
  trust: a version's codeword count is checked against the modules that actually
  remain after the function patterns are placed, which is how a real bug was found
  — alignment patterns that sit on the timing row from version 7 up were being
  skipped, costing five codewords a version. The error correction is checked by the
  property that defines it rather than against a copied table: a correct codeword
  evaluates to zero at every root of its generator, and that arithmetic is written
  out separately in the tests.

- **pinqops can send mail, and alerts can arrive by it.** A relay is configured
  once on the Mail page — host, port, how the connection is protected, the sender
  address — and `email` joins webhook, Slack and Telegram as somewhere an alert
  rule can go. There is a Send-a-test button, because a relay that is wrong should
  say so now rather than at three in the morning.

  **It is a relay, not a mail server, and the page says so in as many words.**
  pinqops does not receive mail, hold a queue, retry on its own schedule or sign
  anything. It hands a message to something that already speaks for a domain and
  reports what that something said — including the refusal verbatim, because "550
  5.7.1 relaying denied" says what no message of ours could.

  **The password is in the vault, and never on the wire by accident.** The
  settings file holds the *name* of a vault entry, so what gets read to render a
  form is a name; the password is fetched at send time, which also means a
  rotation takes effect on the next message rather than the next restart. Two
  refusals are deliberate and both fail closed: if STARTTLS was asked for and the
  server does not offer it, the send **stops** rather than continuing in the clear
  — a downgrade nothing would report — and a password is never sent over an
  unencrypted connection unless that was explicitly allowed. The check happens
  before the command, so the credential is not sent and then regretted.

  **The DNS records, worked out rather than looked up.** Mail from a domain with
  no SPF record and no DKIM signature does not bounce — it is accepted, filed as
  spam, and never mentioned again. The Mail page generates the MX, SPF, DKIM and
  DMARC records for the domain you send as. SPF is a soft fail and DMARC starts at
  `p=none`, deliberately: a strict policy published before the reports show SPF
  and DKIM passing does not fail loudly, it silently destroys delivery of mail
  that was already working. The DKIM value is generated by whatever signs the
  mail, so it is shown as a marked placeholder with the command that produces it
  rather than as something to paste.

  Under it is a small SMTP client rather than `System.Net.Mail.SmtpClient`, which
  cannot open a connection that is TLS from the first byte — which is what port
  465 is, and the port several providers document first. Message bodies are always
  base64: it makes an over-long line, a line starting with a dot, and a byte above
  127 impossible at once, and each of those is a corruption that only shows up in
  somebody else's inbox. Subjects and sender names carrying a line break are
  refused rather than escaped, because a newline in a header is how a second
  recipient is smuggled into a message.

  `docker-mailserver` joins the catalog for anyone who would rather run their own,
  with a note saying plainly that it needs public ports, a matching hostname and
  those DNS records, and will not deliver until all three are done.

- **A traffic summary, from the proxy's own access log.** Requests, errors, bytes
  and the 95th percentile response time per domain, and the busiest routes within
  each. Turn it on and the proxy starts writing a JSON access log that Caddy rolls
  itself.

  **The 95th percentile, not the average.** A mean response time is dominated by
  the many fast requests and says nothing about the few slow ones — which is the
  only part anybody ever complains about. The nearest-rank method is used, so the
  number reported is a request that actually happened and can be gone and found in
  the log.

  **Routes are grouped, not counted raw.** `/orders/1041` and `/orders/1042` are
  the same route and two different paths; counting paths gives a list where every
  entry has one hit, and a summary whose size grows with traffic rather than with
  the application. Numeric, UUID and long-hex segments collapse; a slug like
  `my-first-post` is left alone, because collapsing it would hide a route somebody
  wrote.

  **It is not tracking, and the page says so.** No cookies, no identifiers,
  nothing per visitor, and nothing leaves the machine. The country column is
  exactly what a CDN in front of pinqops already put in a header — there is no
  embedded GeoIP database and no lookup per request — so **the column is absent
  rather than empty** when nothing carried one. An empty column because the header
  is missing means something different from an empty column because nobody
  visited.

  A half-written last line is skipped rather than thrown on. The file is being
  appended to while it is read, so that is its normal state on a busy server, and
  a parser that failed there would fail every time it mattered.

- **Container logs are kept, and searchable.** `docker logs` reads the
  container's own file — which docker rotates, and which disappears entirely when
  the container is recreated. That is what every deploy does, so the output
  explaining why last night's deploy failed is gone by the time anyone asks. Pick
  the containers worth keeping and their output survives them.

  **Every limit here is load-bearing**, because a collector is a thing that writes
  to disk at a rate somebody else controls. At most twenty containers, a byte
  ceiling per container, and collection pauses when free space drops below a
  gigabyte — a full disk stops the database, the proxy and the deploy, none of
  which anyone would trade for a log. The page shows the worst case *before* it is
  turned on and what it is using now.

  Containers are named rather than "all": a dashboard that quietly starts
  recording every container on the host is one that fills a disk somebody else was
  using. Following starts from a minute ago rather than the beginning of time —
  replaying a month of output would write a month into the rotating file and evict
  the very lines somebody is about to look for. Both streams are kept, because
  most containers write their application output to stderr.

  **The search runs newest first and stops when it has enough.** The question
  people bring to a log is almost always "what just happened", and on a log of a
  million lines that is the difference between an answer and a timeout. When it
  stops early it says so, rather than letting the last line shown read as the last
  line there is.

  A regular expression that has been half typed is an answer — "that is not a
  pattern" — not a stack trace, and one that runs too long is abandoned and
  reported rather than silently returning fewer results.

  Admin, including the reads: a container's output is whatever the application
  decided to print, which on a bad day is a connection string or somebody's
  personal data.

- **Point-in-time recovery, for PostgreSQL.** Archive every change as it happens
  and the database can be rebuilt as it stood at any moment, not just as of the
  last backup — the difference between losing a day and losing the minutes since
  the mistake.

  **PostgreSQL and nothing else, and the page says so.** MySQL's binlog and
  MongoDB's oplog can approximate this with different commands and different
  failure modes; Redis has nothing of the kind. Offering "PITR" that means
  something different for each engine would be a promise pinqops could not keep.

  **The archive command refuses to overwrite.** That is not belt and braces:
  postgres treats a successful archive command as "this segment is safely stored"
  and is then free to recycle it, so a command that silently overwrote an existing
  file would report success for a segment it had just destroyed — and the gap only
  appears during a recovery. The settings are appended rather than written over,
  and the page shows the exact lines: anyone letting something edit
  `postgresql.conf` is entitled to read the diff first.

  **Archived changes alone are not a backup.** Recovery replays *forward* from a
  base backup, so a target before every base backup is refused with that reason
  rather than attempted. Retention understands this too: it never drops WAL from
  at or after the oldest base backup it is keeping, because a base backup without
  the changes that follow it restores only to the instant it was taken — a backup,
  but not point-in-time recovery, and the difference is not noticed until somebody
  needs the difference. Retention also never empties the set, which would turn
  "keep 3 backups" into "keep no way to recover".

  The window offered ends at the **last change that reached the archive**, not at
  now. What has happened since is still only in the live server and would be lost
  with it; saying "up to now" would be a promise the archive cannot keep.

  It is not replication and does not survive the disk. Copying the archive
  somewhere else is the offsite backup setting, and this says so rather than
  letting anyone assume otherwise.

- **Object storage buckets, and links that expire.** Create a bucket, browse what
  is in it, and mint a link to one object that anyone can open for an hour without
  signing in.

  **The same credentials the offsite backups use.** Asking for the endpoint and
  keys a second time would mean two places to rotate them and one of them going
  stale — and the bucket a backup lands in *is* a bucket. A separate bucket for an
  application is a different name under the same account.

  **A shared link is arithmetic, not a request.** Presigning computes a signature
  over the URL and the expiry, so there is no round trip and nothing to fail. The
  expiry is part of what is signed: editing it in the URL does not extend the
  link, it invalidates it — which is the whole reason one can be handed out at
  all. Minting one is written to the log, because the link outlives the click.

  **Removing a bucket never empties it first.** Every service refuses while the
  bucket holds anything, and pinqops does not override that — a button that reads
  "remove bucket" must not be the one that deletes what is in it. The dialog says
  so.

  Bucket names are held to the strictest set AWS, R2 and MinIO share, and
  uppercase is refused rather than lowercased: folding it would make `MyBucket`
  and `mybucket` the same bucket in pinqops and two different names in whatever
  the operator typed into their application's configuration.

- **A Databases page: connection strings, and a one-click version upgrade.** The
  catalog could install PostgreSQL, MySQL, MariaDB and MongoDB; moving one to a
  newer version meant knowing the dump command by heart.

  **The upgrade dumps and restores rather than starting the new image on the old
  volume.** Postgres refuses to do the latter at all — its data directory is
  version-stamped. MySQL and MariaDB *will* do it, which works until the release
  where it does not, and the failure mode is a server that starts and serves
  subtly wrong data. A dump is the engine's own supported path between versions.

  **The old container and its volume are never touched.** The new version gets its
  own volume and its own name, so a failure at any point is undone by starting the
  old one again — and nothing is deleted for you afterwards. The thing that must
  not happen here is an upgrade that has destroyed the only copy by the time it
  discovers it cannot finish, so the dump failing means nothing changed at all,
  and a failure after that says in the message which container still has the data.

  **Downgrades are refused, not attempted.** A dump from a newer server usually
  will not load into an older one, and that failure lands *after* the new
  container is running. The page only offers the versions ahead of the current
  one.

  **Redis is listed without an upgrade button.** Its persistence file is the
  database; there is no dump-and-restore path pinqops can drive. A one-click
  upgrade that works for three engines and corrupts the fourth is worse than a
  button that is not there, and the row says why rather than leaving a gap.

  Connection strings percent-encode the password. A generated password containing
  `@` produces a string that parses as a different host, and the failure reads as
  "could not resolve" rather than as a quoting problem. The host is the container
  name, because that is how one container reaches another — a string with
  `localhost` in it works from the server's shell and from nowhere an application
  runs.

  The versions offered are an allow-list, not a format check: the value goes into
  a docker image tag, and "anything that looks like a version" is how a tag
  becomes an image nobody reviewed.

- **Backups can be copied off the server.** Point pinqops at an S3-compatible
  bucket — S3, Cloudflare R2, MinIO, Backblaze — and every snapshot is uploaded
  after it is written, with its own retention count out there. A snapshot on the
  same disk as the database it came from covers a bad migration and nothing else.

  **Signing is written out rather than taken as a dependency.** pinqops ships as
  one self-contained binary and talks to docker through its CLI; pulling in the
  AWS SDK for four operations would add tens of megabytes and a dependency tree to
  a program whose whole shape is not having one. The signature algorithm is a page
  of HMAC, and the tests pin it against **AWS's own published test vector** rather
  than against itself — a signature wrong in any byte is rejected with the same
  message as a wrong password, so a test that agrees with the code proves nothing.

  Two details that are the usual causes of a signature that works until it
  doesn't: the URI encoding is AWS's rule and not the framework's (which is why
  a key with a space in it is the one that breaks), and the query is sorted
  **after** encoding rather than before.

  **Path-style addressing, always.** The virtual-host form needs a wildcard DNS
  record and a wildcard certificate, which MinIO on a LAN does not have. Every
  S3-compatible service accepts path style, AWS included.

  **An upload never fails a backup.** The local snapshot is already written and is
  the copy that matters most; reporting the whole run as failed because a bucket
  was unreachable would hide a backup that did work and raise an alert about the
  wrong thing. The failure is reported beside the success instead.

  A rejected signature says **"check the secret key and the region"** rather than
  passing on S3's own "access denied" — that message sends people to look at
  bucket policies when the answer is a mistyped secret or the wrong region, and
  the region is easy to get wrong because R2 wants `auto` and AWS wants the
  bucket's.

  Fetching a copy back writes to a partial name and renames only when the download
  finished, so a dropped connection cannot leave a truncated file that the
  snapshot list presents as an ordinary backup. It lands in the local snapshot
  directory, which makes restoring an offsite copy the restore that already exists
  rather than a second path with its own failure modes.

- **A Stacks page: compose projects you write yourself.** The escape hatch for
  everything the catalog and the app wizard do not cover. Write the YAML and its
  `.env` in the browser, start it, stop it, pull it, remove it.

  **Nothing is written over a working stack until compose has accepted it.** The
  new text goes to a candidate file beside the live one, `docker compose config`
  runs against that, and only a file compose parses replaces what is running.
  Writing first and validating after would mean a typo leaves the project
  unrunnable *and* takes the operator's last-known-good text with it.

  It is validated **in place**, not through a pipe: `compose config` resolves
  relative paths, `env_file:` and bind mounts against the file's own directory, so
  a check run in a scratch directory answers about a different project than the
  one that would actually run. When it refuses, you get compose's own message —
  which names the line and the key — rather than "invalid YAML".

  A refused save also puts the `.env` back. It has to be written before the check
  (compose interpolates it, so validating against the old one would accept a file
  that fails on the very next command), and leaving the new one beside a rejected
  YAML would change what the *running* stack resolves to on its next restart, from
  an edit that was turned down.

  **Stop never removes volumes**, and removing a stack deletes its files and not
  its data. A button that reads "stop" must not be the one that deletes a
  database. The project name is pinned with `-p` rather than taken from the
  directory, so a file carrying its own top-level `name:` cannot end up as a
  project the dashboard can no longer find.

  Admin throughout, and gated on `stack` — the resource kind reserved for this
  back in the teams work. A stack file is arbitrary containers, arbitrary bind
  mounts and arbitrary published ports on this server.

- **A shell inside a running container, from the browser.** The Console tab could
  run one command and show its output; it can now open a session — type, read,
  type again, with a working directory and shell variables that persist. `psql`,
  `redis-cli`, `ls` and `cat` work.

  **It is line-oriented, and the tab says so rather than pretending.** Anything
  that redraws the screen — `top`, `vim`, a progress bar — will not work, because
  there is no pseudo-terminal. Making one would mean escape sequences, cursor
  addressing, and a client that understands both: xterm.js inlined into the
  single CSP-hashed script, taking the page from 470KB to about 750KB with the
  hash recomputed every release. A console that is almost a terminal is worse
  than one that is honestly not.

  It is **one** `docker exec -i`, not one per line. A shell restarted for every
  command has no working directory, no variables and no open `psql` session —
  which is to say it is not a shell, it is the one-shot exec that already
  existed.

  **Every command is written to the audit trail as it is typed.** This is the one
  place in pinqops where an operator can do anything at all, so "a console was
  opened" is not enough of a record.

  Output is capped per command. A `cat` of a large log would otherwise push
  megabytes through a socket that carries 64KB at a time, for minutes, during
  which the console accepts nothing else — so the cut is made visible and the
  next command starts fresh. The session is admin-only, gated on the container
  like every other per-container route, and inherits the channel's idle and
  lifetime limits — closing the tab or the dialog ends the shell rather than
  leaving one running against a pane nobody can see.

- **A badge when a container's image has a newer version.** Once an hour pinqops
  asks each registry what the tags your containers run point at now, and marks
  the ones that have moved. A redeploy picks the new image up; nothing is pulled
  or restarted on its own.

  **It compares digests, not tags.** A tag is a name that moves — `postgres:16`
  today and `postgres:16` last month are different images — so comparing the text
  answers nothing. And it compares the *repo digest* rather than the local image
  id: the id is the local config's hash and differs between architectures for the
  very same published image, so an id comparison would show an update on every
  arm64 host, forever.

  **It asks, it never pulls.** A HEAD request returns the manifest digest in a
  header; pulling to find out whether there is something to pull would cost the
  bandwidth this exists to save, hourly, per image.

  Getting the reference right is most of the work, and each way of getting it
  wrong produces a request to the wrong place rather than an error: a colon can be
  a port or a tag, a first segment without a dot is a namespace and not a host,
  and an unqualified Docker Hub name means `library/` — `postgres` asked for
  directly answers 401, which reads like a credential problem and is a missing
  prefix.

  **An unknown digest is not an update.** If the check could not complete —
  registry unreachable, image built locally and never pulled — that is reported as
  a problem, not as a badge. A badge that also means "something went wrong
  somewhere" is a badge nobody acts on.

  Private images use the credentials from the registry list; public ones never see
  them. The check is capped per pass and says so when it caps, because a registry
  asked about two hundred images every hour will rate-limit the ones that matter.

- **Private registries.** Record a registry, the account, and which vault entry
  holds its password, and pinqops signs the docker daemon in — so a deploy can
  pull from somewhere other than a public GHCR repository.

  **The password never becomes a command-line argument.** An argument list is
  readable by every user on the host through `ps`, so `docker login
  --password-stdin` is not a nicety — it is the only form that does not publish
  the credential to the machine. The process runner learned to write stdin for
  exactly this; the username is still an argument, because a name is not a
  secret.

  **Nothing in pinqops' own files holds the password.** The registry entry
  records the *name* of a vault secret, not its value. That is not tidiness: this
  file is read to render a list, and a list is exactly the kind of thing that
  gets logged, diffed and pasted into a support message.

  Adding a registry signs in immediately rather than storing and hoping. A
  credential nobody has tried is a credential nobody has checked, and the next
  deploy is a poor time to discover the token was pasted with a trailing space.
  Removing one signs the daemon **out** as well — otherwise "removed" means only
  "removed from this list" while pulls keep working.

  Docker Hub is normalised to the URL docker actually keys its auth file by. No
  operator would think to type `https://index.docker.io/v1/`, and typing
  `docker.io` instead would have failed with a message about DNS.

  What happens after a successful login is docker's: the credential lands in the
  daemon user's `~/.docker/config.json`, base64 and not encrypted. pinqops does
  not pretend otherwise — the vault is what keeps it out of *pinqops'* files.

- **Volumes can be created, inspected, browsed and removed.** The Storage page
  listed them and nothing else; a volume is where an app's data actually lives,
  and looking inside one meant a shell on the server.

  **Browsing runs in a throwaway container with the volume mounted read-only.**
  The dashboard has no access to `/var/lib/docker` and should not be given any —
  a volume driver may not even put the data on this disk. You can walk the
  directories and download a single file.

  **The path is the one value pinqops takes that has structure**, and that is
  what had to be checked. It is concatenated onto the mount point inside the
  container, so `../../etc/shadow` would read the host's files through the bind
  mount — and look like an ordinary listing while doing it. Segments are folded
  rather than rejected, because `..` is exactly what the parent-directory button
  produces; only a fold that would leave the volume is refused, and refused
  before docker is called at all.

  A download is **copied out and served from a scratch directory** rather than
  read through the process runner. That returns text, and a text round-trip turns
  every binary file into a corrupt one without saying so.

  Reading what is inside a volume is **admin**, unlike listing the volumes: it is
  the application's own data — the rows of a database, the files somebody
  uploaded. Removing a volume is never forced; docker refusing while a container
  still refers to it is the only warning anyone gets, and there is no undo.

- **The Images page does the whole lifecycle now.** It listed what was there and
  offered one prune button; it can pull, tag, inspect, show the layers, and
  remove.

  **A pull runs in the background and the page polls it.** A pull is the one
  docker operation with no useful upper bound — seconds for a small image, half
  an hour for a machine-learning base image on a domestic uplink. Holding the
  request open for that means a proxy timeout somewhere in the middle and a
  dashboard reporting failure for a pull that is going perfectly well. Two people
  asking for the same image at once get one pull, not two.

  **Remove is never forced.** Docker refusing to delete an image a container is
  running from is the only thing standing between a tidy-up and an app that
  cannot restart, and overriding that is not a decision to make on somebody's
  behalf. The dialog says so instead of offering a `-f`.

  **The new prune button is a second button, not a checkbox on the first.** The
  existing one removes dangling layers. The new one removes every image no
  container is using — which includes the previous version of each of your apps,
  the thing a rollback needs and cannot get back without a pull that depends on
  the registry still having it. The confirmation says that in those words.

  Inspecting an image and reading its layers are **admin**, unlike listing them:
  the inspect payload carries every build-time variable baked into the image and
  the history carries the Dockerfile commands verbatim, which is where a token
  passed as a build argument ends up. The container equivalent already masks
  those for non-admins; there is no useful masked version of a layer's command.

  `ImageRetentionPruner` — what keeps the last few `sha-*` images for rollback
  after a deploy — is untouched by any of this.

- **A Jobs page: run a command on a schedule.** A nightly database dump, a cache
  sweep, a report. It runs either inside a container that is already up, or in a
  throwaway container from an image — and either way with a timeout, an optional
  retry, and the output of every run kept.

  **The command is a list, never a line.** A single text box would have to be
  split by something, and every splitter is a shell grammar with quoting rules —
  which is how `--filter 'name=my app'` becomes three arguments, and how a value
  typed into a form becomes a second command. Each word goes to docker as one
  argument and nothing in it is interpreted, so there are no pipes and no
  redirection; the page says so rather than letting anyone find out.

  **Everything wrong with a job is caught while it is still a form value.** A
  cron expression that does not parse, a container name with a space in it, an
  image reference starting with a dash — each is refused on save. The alternative
  is a job that fails at three in the morning in a log nobody is reading.

  **One run of a job at a time.** A job that takes eleven minutes on a ten-minute
  schedule would otherwise pile up until the host gives out, and the second copy
  of a database dump is not a second dump — it is two processes writing one file.
  A failure goes to the alert channels, once, naming the exit code.

  **It does not retry unless asked to.** A job that writes something is not
  automatically safe to run twice, and pinqops has no way to know which kind this
  one is.

  Admin throughout, reads included: a job's definition is a command run on this
  server, with the container it runs in and the arguments it is given, and that
  says more about the infrastructure than the container list does. Every read of
  it is audited for the same reason.

- **The number of copies can follow the load.** pinqops already samples every
  container once a minute for the alert rules; the same readings can now add a
  copy when an app is working hard and remove one when it is not.

  It is built on the shape the alert evaluator already proved against real hosts:
  a reading has to stay past the target for a whole window before anything
  happens, and every change starts a cooldown. One busy minute is not a reason to
  start a container, and a controller that reacts to a single sample oscillates
  between two counts forever.

  **It adds one copy at a time** rather than jumping to a computed target. On one
  server the jump reads well and behaves badly: the reading that justified it was
  taken before any of the new containers existed, so it is always based on a load
  the app was not yet spreading. One step, then look again after the cooldown,
  reaches the same number without overshooting into swap.

  **Scaling down needs more than scaling up.** It waits for the readings to be
  *well* under the target, not merely under it — dropping a copy the moment a
  reading dips past the line would put the app straight back over it with one
  fewer copy, and then back again. The cooldown after removing a copy is longer
  than after adding one, for the plain reason that a copy nobody needed costs some
  memory and a copy that was needed costs an outage.

  **A reading it cannot take is not a quiet one.** If docker cannot be reached,
  the count holds and any window in progress starts over. A controller that
  scaled down because it could not see would take an app apart during an outage.

  Every change goes through the same gate a deploy takes, so it can never
  recreate containers underneath one, and every change is written to the audit
  trail with the reading that caused it — attributed to pinqops rather than to
  whoever last logged in, because nobody asked for it.

- **A release can happen without the app going down.** Until now a deploy stopped
  the running containers and started the new ones, so the app was gone for a few
  seconds. Turn this on and the new version starts *beside* the old one under a
  second name, and only gets the traffic once it has come up healthy and answered
  a request. Nothing touches the version that is serving until the new one has
  taken over — a failure at any step before that leaves the live app exactly as
  it was.

  **One rule makes every crash recoverable:** which colour is live is written to
  disk only *after* the proxy has accepted the new routes. So the record always
  describes what the proxy is actually pointing at, and whatever a restart finds,
  it can tell which half of the switch happened. Writing it first would leave a
  record of a switch that never took effect — and the next proxy restart would
  quietly finish a deploy that had been abandoned.

  At startup the dashboard puts the routes back on the recorded colour, and
  **never the other way round.** Deciding that the other colour "looks more
  alive" and moving to it would silently complete a cutover that a failed deploy
  deliberately refused, which is how a version that never passed its health check
  ends up serving.

  **Rollback becomes a proxy reload.** The previous version is left running with
  no traffic, so going back to it is not a pull and a restart — it is switching
  one name, in well under a second, to containers that never stopped and have
  already proved they run here. The cost is stated rather than buried: the app
  uses twice the memory between releases, and the switch that buys it can be
  turned off.

  **It refuses projects it would break, and says which line.** Two colours are
  two compose projects over one file, so everything compose scopes by project is
  duplicated. That is right for containers and wrong for a volume declared in the
  project: blue and green would each get their own, and a database would start
  empty on the first switch and swap back to stale data on the next. There is no
  warning for that and no way to notice until the data is gone. A second service,
  a fixed `container_name:`, and a still-published host port are refused for
  reasons of the same kind. Every reason is listed at once — being told them one
  per attempt is three rounds of the same conversation.

  The compose file is **never copied** to give the second colour its own. A copy
  elsewhere on disk breaks every relative path in it — bind mounts, `env_file:`,
  build contexts — and those belong to whoever wrote them, not to pinqops.

  One bug this exposed and fixed: applying an environment change ran `up` with no
  project name, which on a two-coloured app would have taken the name from the
  compose file and started a **third** project — a second copy of the app that
  nothing routes to, holding the same external volumes, from a form about
  environment variables.

- **An app can run more than one copy, and the proxy spreads requests between
  them.** One copy means every restart is a gap and every slow request is in the
  way of the next one. Set the number of copies on the Deployments page; the
  proxy's routes change as soon as you save, and the containers follow on the
  next deploy — the page says so, because "I set it to three and nothing
  happened" is otherwise the obvious conclusion.

  **The proxy learns the set, it does not count it.** The upstream is the docker
  network name every copy answers on, re-resolved every few seconds. Writing N
  upstreams into the configuration instead would mean regenerating it on every
  scale, every crash and every restart — and any regeneration that was missed
  leaves the proxy sending traffic to a container that no longer exists, or
  ignoring one that does. Neither is visible from the dashboard, and both look
  like an application fault.

  Requests go round-robin by default, or to whichever copy is least busy, or —
  for an application that keeps a session in memory — always back to the same
  copy, by visitor address or by cookie. The cookie's name is fixed rather than
  typed in: it is written into the proxy configuration and set on the visitor's
  browser, and there is nothing to gain from letting it be anything else.

  **It requires that the proxy publishes the app's host port**, and says so
  rather than letting the deploy find out. Two containers cannot bind one host
  port; docker's own answer is "port is already allocated", which reads as
  somebody else taking the port instead of the app taking it from itself. Taking
  the port back sets the app to one copy in the same step, so a button about
  ports cannot leave a deploy that fails.

  **A bad balancing setting costs balancing, not the route.** An alias or policy
  the proxy will not emit is skipped and reported, and the app keeps answering
  from its single container. Taking a site offline to protect it from being
  slightly slower is the worse trade by a wide margin.

  One thing worth naming because it is the sharpest edge in the design and looks
  like the dullest: a preview environment never inherits production's network
  name. If it did, every open pull request would join production's pool and start
  answering production's traffic from an unreviewed branch.

- **A deploy can wait for the application to answer, not just to start.** Docker
  knows whether a process is running and, when the image declares a HEALTHCHECK,
  whether that script is happy. Most images declare none — and a process that
  started and then failed to bind its port is "running" by every measure docker
  has. One HTTP request is the whole difference between started and serving, and
  a deploy that reports green for the first is the most expensive kind of wrong.

  It runs **after** the container check, never instead of it: asking an
  application for a page while its container is still starting answers nothing,
  and the compose check is what knows when that is over.

  **Two answers in a row by default.** A process that binds its port before it
  has finished loading answers once and then stops; a single answer would call
  the deploy green in exactly the window where the application is least able to
  serve.

  **Off until you turn it on.** A gate that can fail a deploy is not something to
  switch on underneath an app during an upgrade — an app with no route at `/`
  would start failing deploys that had worked for a year, and the first anyone
  would hear of it is a red deploy at three in the morning.

  **Once it is on, a probe that cannot be run has not passed.** No address, an
  unreadable inspect, a fallback that will not start — each fails the deploy. A
  gate that quietly stops gating is worse than no gate at all: it reports the
  same green either way.

  The request goes to the container on its own docker network. That is
  unreachable on Docker Desktop and anywhere else the bridge is not routed to the
  host, and the symptom — connection refused — looks exactly like a broken
  application. On the first such failure the probe asks from inside the network
  instead, and says so in the log.

  The path is **refused rather than tidied up** if it is not one. A `/health
  check` silently saved as `/` would leave you reading a green deploy that
  checked something else entirely. Numbers are clamped, and the page re-renders
  from what was actually stored so there is nothing to compare.

- **An app can hand its host port to the proxy.** Until now an app published its
  own port, which meant only one version of it could ever run: two containers
  cannot bind one port, and there is no way to hand a listening socket over. That
  is the single fact standing between pinqops and replicas, and between pinqops
  and a deploy with no gap. Hand the port to the proxy and both become possible —
  the app stays reachable at exactly the same address, whether or not it has a
  domain.

  **The switch costs one brief moment, and the dashboard says so** rather than
  implying it is free: the app has to release the port before the proxy can bind
  it. It happens once, when you switch — not on every deploy.

  Every step after that moment is undoable, and that is the point. A failure
  halfway would otherwise leave an app publishing nothing and a proxy that does
  not know about it — an app that is simply gone. If anything fails, the compose
  file, the environment and the proxy's ports all go back.

  The compose project is edited, not regenerated. It invites edits — it says "add
  whatever else YOUR application needs here" — so regenerating would delete the
  volumes, environment and extra services someone put there. The published
  mapping is commented out rather than deleted, so taking the port back restores
  the original file byte for byte. If the file does not publish its port the way
  pinqops writes it, pinqops **refuses and says which line is wrong**: a wrong
  edit here does not produce an error, it produces an app that is quietly
  unreachable or a port collision at the next deploy.

  Two things the proxy now reports, because both look like application faults
  otherwise. A container's published ports are fixed when it is created, so a
  configuration that has gained a port describes a route the running proxy cannot
  serve — that is shown, with a **Republish** button, rather than repaired
  silently, because recreating the proxy is a brief outage for every domain and
  that is the operator's call. And a proxy that is down while it holds an app's
  port *is* that app being down: the page says which apps, and offers to take
  their ports back.

  That second one is also **sent to the alert channels**, once, naming the apps —
  nowhere else on the host would explain why an address that has nothing to do
  with the proxy stopped answering. It is sent when the state changes, not every
  minute for as long as it lasts, and again when the proxy comes back. It is a
  notice from pinqops rather than a threshold rule, so it does not appear on the
  Alerts page as a rule nobody wrote.

  **There is deliberately no automatic rescue.** Nothing rewrites compose files
  in the background when the proxy dies — doing that while a deploy may hold the
  project's lock, or while the proxy was stopped on purpose, is how a short
  outage becomes a set of corrupted projects.

  **One diagnostic had to change with it.** The deploy's "you are publishing a
  port the image never exposes" warning read the answer off the container's
  published ports, so it fell silent for exactly the apps that gained the most
  from it: an enrolled app publishes nothing of its own. It reads
  `PINQOPS_CONTAINER_PORT` now. A proxy route aimed at a port nothing listens on
  reads as a proxy fault rather than an application fault, which is the most
  expensive way to be wrong about this.

- **A newly published app gets its own network.** Until now every app and every
  catalog service shared one — which is what let the proxy reach them by name, and
  also what let any app reach any other app's database by name. On a server
  running one person's projects that is convenient; on one running a customer's
  app next to an internal tool it is a door nobody opened on purpose.

  An app published from now on lives on `pinqops-app-<id>`. The proxy is
  connected to it, so its domain and its published port work exactly as before —
  and nothing else is, so it cannot reach another app's database until somebody
  connects the two. Giving it access is `docker network connect`, which the
  Networks page and the container's own Networks tab already do; opening an app's
  network now explains what it is and what connecting something to it means,
  because a network with one container on it and no explanation is not a feature
  anyone would find.

  **Apps published before this are untouched.** They stay on the shared network,
  because rewriting their compose file to move them would cut them off from the
  database they are already using. Catalog services stay there too, for the same
  reason — so the isolation is between new apps and everything else, not a
  retrofit that breaks what works.

  One failure this had to handle: a container's networks belong to the container,
  so a proxy that has just been recreated — after a port change, or a DNS
  provider change — is on none of them. Every create now reattaches it to every
  app network, because a domain going down is not something anyone would connect
  to "the proxy was reinstalled".

- **Running behind a CDN, done correctly.** Tell pinqops that Cloudflare sits in
  front of it and the proxy starts believing the forwarded headers from
  Cloudflare's networks — which it fetches and stores, so the list can be
  refreshed rather than going quietly stale.

  **This does not make pinqops a CDN, and the page says so.** A content delivery
  network is machines in many places answering from the one nearest each visitor;
  that cannot be built on one server and nothing here pretends otherwise.

  What it fixes is real, though. Behind a proxy every request arrives from the
  proxy's address, so until now a rate limit put the entire internet in one
  bucket, a log recorded the CDN instead of the visitor, and a country header
  could have been written by anyone. Naming the networks whose forwarded headers
  are believed is what makes all three mean something — and it is the
  prerequisite for the traffic analytics to come, which cannot trust a country
  header from a proxy it was never told about.

  Optionally, static assets get a `Cache-Control` the CDN can act on. It applies
  to a fixed list of asset extensions and **never to pages**: caching a logged-in
  view at the edge is how one person's page reaches another's browser. It is off
  by default, because pinqops does not know which of an app's paths are genuinely
  immutable.

  A network that is not valid CIDR is dropped and reported rather than written —
  one malformed line in that list makes Caddy refuse the whole file, which would
  take every domain on the server down with it.

- **pinqops can write the DNS record for you — on Cloudflare.** A domain on the
  Domains page gets a **Point here** button that creates or updates its address
  record to this server's public address, then runs the same preflight the add
  form does and says whether it has propagated yet. A record that has just been
  created is not yet a record the world can see, and reporting "done" before it
  resolves is how someone concludes the button did nothing.

  **An honest asymmetry:** pinqops can answer a certificate challenge through
  three providers but can only write records through one so far. Configure
  Route 53 or DigitalOcean and wildcard certificates work while the button stays
  hidden, with the reason shown rather than a control that fails when pressed.
  With no provider at all nothing changes: records are added by hand and the
  preflight tells you whether they landed, exactly as before.

  **Point here asks whether to proxy.** Proxied (orange cloud) is the default;
  DNS-only (grey cloud) remains a choice. Proxied terminates visitor TLS at
  Cloudflare, so SSL/TLS mode there should be Full or Full Strict and the origin
  still needs its own certificate. Edge mode turns on automatically with a
  proxied write so logs and rate limits see the visitor rather than the CDN, and
  the DNS check treats Cloudflare addresses as a match when edge mode is on.

  When Cloudflare refuses, its own words come back: a token without `Zone:Edit`
  and a zone on another account are different problems, and "DNS update failed"
  makes you guess which.

- **Wildcard certificates, through a DNS challenge.** Configure a DNS provider —
  Cloudflare, Route 53 or DigitalOcean — and a domain like `*.example.com`
  becomes something pinqops will route and get a certificate for.

  Until now a wildcard was refused, and that refusal was correct rather than a
  gap: HTTP-01 proves control of a name by serving a file *at that name*, and
  there is no host at `*.example.com` to serve one from. The only proof that
  covers a wildcard is a DNS record. **Without a provider configured the refusal
  is unchanged, down to the message** — and only a single leading label is ever
  accepted, because `*.*.example.com` and `a.*.example.com` are names no
  certificate authority will issue and taking them would only defer the failure.

  **Only the wildcard moves to the DNS challenge.** An ordinary domain keeps
  proving itself over HTTP-01, which needs no credential and no access to your
  zone — switching everything over would mean one wrong token breaking
  certificates that were working.

  The provider token is a secret from the vault, and pinqops stores only its
  *name*. The value is resolved when the proxy container is created and handed to
  it in its environment: the Caddyfile is regenerated constantly and sits beside
  a config two processes write, which is not where a credential that can edit an
  entire DNS zone belongs. Because a container's environment is fixed when it is
  created, changing the provider or the secret replaces the proxy container
  rather than reloading it — the certificates survive, they are in a volume.

- **A profile for apps that keep connections open.** WebSockets, server-sent
  events, long polling, a streaming API: a domain can now say it carries those,
  and the proxy stops doing the two things that break them.

  Caddy forwards a WebSocket upgrade whether or not you ask it to — that part was
  never the problem. What breaks these connections is everything around it: the
  proxy collecting a response that is never going to end, and a read timeout
  firing on a connection that is idle by design rather than stuck. Both are
  turned off for the upstream when the switch is on, and nothing changes for a
  domain that leaves it alone.

  The same switch is available for an app reached by `server:8080` rather than by
  name, because it has exactly the same problem.

  One caveat the panel states plainly: with more than one replica an open
  connection stays on the replica it started on and is not moved, so a client
  that reconnects may land somewhere else. The app still has to cope with that.

- **Rate limits per domain.** A domain can now cap how many requests it accepts,
  with anything over the limit answered 429 before the app ever hears about it.
  Counting is by client address, or by the value of a request header when one
  request per API key or per tenant is the unit that matters.

  There are **two windows**, and that is the design rather than an option: one
  limit has to choose between stopping a burst and stopping a grind, and it
  cannot do both — set it low enough to blunt a burst and ordinary page loads
  start failing, set it high enough for a page load and someone can hold that
  rate all day. So a domain gets a short window sized for what a browser does in
  a second and a long one sized for what a person does in an hour. Either alone
  works; the pair is what makes it usable.

  Off by default, because a ceiling that fires on a real user is worse than no
  ceiling and there is no number worth guessing for an app whose traffic pinqops
  has never seen. Switched on with neither window filled in is refused and said
  so, rather than rendering a block that looks enforced and enforces nothing.

  This needs the rate-limiting module, which stock Caddy does not have — it comes
  from pinqops' own Caddy build, so a proxy installed before that image existed
  has to be reinstalled before a limit takes effect.

- **The Domains page has a settings panel** for the two things above: the
  response headers and the rate limit for one domain, including the HSTS and CSP
  switches that previously had no way to be turned on. It is a separate route
  from adding a domain, so changing a header cannot re-resolve the app or repoint
  the route as a side effect — and a setting the generator refused comes back and
  is shown instead of leaving the panel looking as though it applied.

- **Security headers on every domain.** The proxy now adds four response headers
  to each domain it serves: content-type sniffing off, framing limited to the
  same origin, the referrer trimmed to what browsers already default to, and the
  three device permissions nothing needs by accident. They apply to domains you
  already have, without anything having to be re-saved, and any domain can turn
  them off.

  Each of the four was picked because it cannot plausibly break an application
  sitting behind a reverse proxy — framing is same-origin rather than denied,
  because an app that frames its own pages is ordinary and blocking that to stop
  something same-origin already stops would be a bad trade.

  Two headers are **opt-in**, and for different reasons. HSTS is the one sticky
  header here: once a browser has seen it, it refuses plain HTTP for the whole
  lifetime and there is no way to reach those browsers and take it back — not a
  decision to make for somebody's existing domain during an upgrade. A
  Content-Security-Policy has no useful default at all: any policy general enough
  to apply everywhere would break nearly every app with inline anything, and one
  loose enough not to would protect nothing. Both are set per domain through the
  API; the dashboard grows a panel for them alongside rate limits.

  Everything emitted is checked first, because `domains.json` is written by two
  processes and read without validation: enumerated values fall back to the safe
  default rather than reaching the file, the HSTS lifetime is clamped at two
  years, and the two free-text policies are refused outright if they carry a
  quote, a brace, a backslash or a control character — which would otherwise
  close the quoted token and escape into the surrounding Caddy block. A refused
  header is reported and costs only itself; the route still works.

- **Teams.** A team is a group of people (and API tokens) that resources can be
  granted to — an app, a container, a server. It answers *which* things someone
  may act on; it deliberately does not answer *what* they may do, which stays
  with the viewer / deployer / admin role exactly as before. The two are
  orthogonal, and the effective permission is the lesser of them: a viewer in a
  team with full access still cannot deploy, because the role refuses first.

  Nothing is enforced yet. Teams and grants can be created and read through the
  API, and the next change is what makes them decide access — so this one
  changes nobody's permissions. That is intentional: an authorization change
  that alters who can do what on the day it lands is one nobody can review.

  Granting is admin-only, including for a deploy-scoped API token, because
  granting is how access is widened and a principal must not be able to widen its
  own. Reading is not blocked but filtered: you see the teams you are in and the
  grants they hold, and nothing about anyone else's.

  Teams and grants live in one file, which is not an implementation detail —
  deleting a team has to delete its grants in the same write, or a team id
  created again later would inherit access nobody granted it. A grant names the
  environment as well as the resource, because a container called `postgres` on
  staging and one on production are different things and a grant that ignored
  which host it meant would hand out production by way of staging. A file that
  cannot be read grants nobody anything, so a corrupt one refuses every
  non-admin while leaving an admin able to repair it.

  On an install that already has users, a **Default** team appears holding all of
  them — admins as owners — and nothing is granted to it. Because a resource with
  no grant behaves exactly as it did before teams existed, an install with that
  team and one with no teams at all are indistinguishable: it is somewhere to
  grant *from*, not something that narrows anything. It is created once, so a
  team deliberately deleted does not come back on the next restart.

  Requests that name no app now resolve to the first app the caller can *see*
  rather than simply the first, so a member of one team never silently operates
  on another's because it happens to sort first. An app someone cannot see is
  reported in exactly the words an id that does not exist gets — distinguishing
  them would let anyone list every app on the server by trying ids and reading
  which refusal came back.

- **WebSockets, and a way to prove they work.** pinqops can now serve WebSocket
  routes, which is what the container console and live log tailing will need.

  A socket authenticates like every other request, and that is the point rather
  than an implementation detail: a browser cannot set an `Authorization` header
  on a WebSocket handshake, and the token must not go in the query string where
  it would land in every access log and proxy trace on the way — so it travels in
  the subprotocol list, which is a header. Everything after that is unchanged:
  the same scope table decides what a socket may reach, the same policies refuse
  it, and the same audit line records it. There is no second authorization path
  to keep in step. An agent that can set headers may keep using `Authorization`.

  Every socket is bounded, because an open connection is server state that no
  request timeout applies to: a message over 64 KB, a connection silent for five
  minutes, and a connection older than two hours are each closed with a status
  saying which limit it hit — so a disconnect does not send anyone looking at
  their proxy for a problem pinqops caused.

  The first route is a diagnostic: `GET /api/ws/ping` echoes what it is sent.
  WebSockets are the part of a self-hosted setup most likely to be broken by
  something in between — an nginx without `Upgrade` headers, a proxy that
  buffers, a tunnel that drops silent connections — and the features that need
  them are the worst place to find out, because they fail with an empty pane and
  nothing to read. It is admin-only and has no button in the dashboard yet;
  reach it with any WebSocket client.

- **Alerts.** A new **Alerts** page puts Grafana-style threshold rules on the
  server's own resources — CPU, memory, swap, disk and per-core load average —
  and on its containers: CPU, memory, and whether a container is down, unhealthy
  or stuck restarting. One rule can watch a single container or **all** of them,
  tracking each as its own series so one noisy container neither fires for the
  rest nor silences them.

  A rule only fires once its condition has held for a window you choose, so a
  one-second spike never pages anyone, and it resolves on its own when the
  condition clears. Rules can be silenced for a period — evaluation carries on
  and the page keeps showing the real state, so a muted rule is visibly muted
  rather than invisibly broken, and a firing that a silence held back is
  announced when the silence lifts rather than lost.

  A series that stops reporting is written off as *no data*, never as resolved —
  a metric going away is not evidence that the problem did. The firing episode
  stays open across the gap, so if readings come back healthy you still get the
  all-clear for the page you got.

  Alerts go to the channels pinqops already had — **webhook, Slack and
  Telegram** — now configured server-wide rather than per app, because no app
  owns "host CPU above 90%" and alerting matters most on a fresh install where
  no repository is connected yet. Deploy-event notifications stay next to their
  compose project (the CLI on the runner reads that file), and both are now
  edited on the same page; Settings links to it.

  The dashboard keeps a rolling window of samples on disk — 48 to 72 hours at one
  a minute, a few MB — and charts each metric with your threshold drawn across
  it, alongside what the metric reads *right now* next to the threshold field, so
  a number can be chosen rather than guessed. A count in the topbar makes a
  firing alert visible from any page, and every transition is written to an alert
  history. Alerts are readable by any role; creating, silencing and editing them,
  and the channels they send to, are admin-only.

  Alerts watch the server the dashboard runs on. Restarting the dashboard does
  not re-page an alert that is already firing, and a `for` window that was in
  progress before a long outage starts again rather than firing on the first tick
  back. Full details in [the Alerts wiki page](docs/wiki/Alerts.md).

- **Multiple Docker hosts.** **Settings → Servers** registers remote servers
  reached over SSH; a switcher in the topbar points the Containers, Images,
  Storage, Networks and Apps views at whichever one you pick. The machine the
  dashboard runs on is always there as **Default** (`local`).

  Connecting a host needs an SSH user in its `docker` group (which is
  root-equivalent *there*), a private key, and the host's public key —
  `ssh-keyscan -t ed25519 <host>` gives you the latter. pinqops stores the key
  encrypted, writes it out `0600`, pins the host key, and manages a marked block
  in `~/.ssh/config`, leaving anything you wrote around it alone. **Test**
  proves the whole path in one click.

  An environment can be marked **read-only**, which refuses every change made
  through it whoever is asking — a role says what a person may do, this says
  what the host permits. An unknown environment id is refused rather than
  falling back to the local daemon, because quietly acting on the wrong server
  is the failure that matters here.

- **A light theme.** Every colour in the dashboard is now a token, so the whole
  page — surfaces, hairlines, log panes, the JSON tree, the chart palette — flips
  in one swap. Pick it from the topbar's sun/moon button or **Settings →
  Dashboard preferences → Theme**, where **Follow the system** tracks the OS and
  reacts live when it changes. The choice is stored per browser.
- **Search over your repositories.** The GitHub view's repository dropdown now
  has a filter box above it. Terms match in any order (`pinq demo` finds
  `pinqponq/pinqops-demo`), a counter shows how much of the list is left, Enter
  picks the top hit and Escape clears the filter. Whatever is currently selected
  stays listed however you narrow the search, so the publish panel below never
  loses the repository it is acting on.

### Security

- **A viewer could read any app's password and any container's logs by changing
  the case of a path segment.** ASP.NET Core routing matches literal path
  segments case-insensitively, but the scope table and the per-container
  ownership gate matched them with ordinal string patterns. So
  `GET /api/APPS/<id>/credentials` reached the same handler as the lowercase
  form while being classified as an ordinary read — handing a viewer an installed
  catalog app's plaintext password, and every container's `logs`, `inspect` and
  `top`. `POST /api/APPS/<id>/uninstall` skipped the ownership check entirely, so
  a deployer could destroy a container another deployer owned. Literal segments
  are now compared case-insensitively (ids stay verbatim — container names are
  case-sensitive to docker), and the ownership gate fails closed: a request in a
  per-container route family that cannot be resolved to a container is refused
  rather than run ungoverned. Successful uppercase reads also left no audit line
  at all, since the audit filter skipped anything classified as a read.
- **`GET /api/notifications` handed the deploy webhook and Slack URLs to any
  role.** An incoming-webhook URL is itself the credential — anyone holding one
  can post into the channel — which is why `/api/alerts/channels` was already
  admin-only. The same secret on the other route was world-readable. It is
  admin-only now, and the card is gated in the UI to match.
- **`docker inspect` and the container list leaked catalog app passwords.** The
  redactor masked `Config.Env`, but the app catalog puts generated passwords in
  argv — `redis-server --requirepass …`, `nats --auth …`,
  `surreal start --pass …` — so they came through in `Config.Cmd`, `Args` and
  `Path`, and in the container list's `Command` field, which sits at the plain
  read scope. All of them are masked below admin now.
- **A deploy-scoped token could delete backup snapshots.** `/api/backups/run`
  was matched as a string prefix, which also covered
  `DELETE /api/backups/<targetId>/snapshots/<snapshot>` for any target id
  starting with `run` — an otherwise admin-only deletion. It is matched on path
  segments now.
- **Every API token authenticated as the same principal.** All tokens resolved to
  the literal user `api-token`, so container ownership gave no separation between
  them: any token could manage and destroy what another token had created. Each
  token now authenticates as `token:<id>`, which no account can collide with
  (`:` is rejected by the username validator). Ownership records written before
  this change name no current principal and must be reassigned by an admin.
- **One valid credential bought unlimited password guessing.** The login throttle
  counted failures per client address only, and cleared the whole bucket on any
  success — so four wrong guesses at `admin` followed by a legitimate login as
  oneself reset the counter, indefinitely. Failures are now counted per account
  as well, and a success clears only the account that actually verified.
- **A password hash whose digest was lost accepted every password.** `Verify`
  derived a key to the length of the stored digest, and for an empty digest
  segment that is zero bytes — which compares equal to itself. The digest length
  is checked before deriving anything.
- **A session could be resurrected after being revoked.** Sliding the idle expiry
  rewrote the entry through the dictionary indexer, which re-added a token that
  a concurrent password change, role change or account deletion had just removed,
  giving it another full lifetime. The slide is now a compare-and-swap that loses
  to the removal.
- **A viewer or deployer could not sign out or change their own password.** Both
  routes fell to the coarse admin write default, so `logout` never revoked the
  session server-side while the UI reported a successful sign-out, and there was
  no way for a non-admin to rotate a password they believed was compromised.
- **`install-service` wrote the systemd unit world-readable before restricting
  it**, briefly exposing an embedded TLS certificate password; it is now created
  `0600` before any bytes are written. `--host` was also interpolated unquoted
  into `ExecStart`, so a value containing a space appended arguments to the
  service command line. Using `--cert-password` now warns that the password also
  lands in the process command line, where any local user can read it.
- **The runner's `.service` file contents went to `systemctl start` unchecked.**
  Whatever that file held became a unit name — or, with a leading dash, a
  `systemctl` flag. It is now validated the same way the fallback scan filters
  unit names, and `--` separates it from the flags.

- **Catalog apps now publish their ports on `127.0.0.1` instead of every
  interface.** A bare `-p host:container` binds `0.0.0.0`, so installing Redis,
  MongoDB, Elasticsearch or Kafka on a host without a firewall put an
  unauthenticated database on the internet the moment the install finished.
  Ports now bind to loopback; reach an app through the **Domains** page (which
  gives it a real hostname and HTTPS) or a tunnel, or tick **Publish on all
  interfaces** in the install dialog to get the old behaviour. That checkbox is
  admin-only. **Apps installed before this change keep their existing binding**
  until they are reinstalled.
- **Apps that shipped without authentication now generate a password.** Redis
  and KeyDB get `--requirepass`, MongoDB a root user, ClickHouse a user,
  RabbitMQ a user (was `guest`/`guest`), Grafana an admin password (was
  `admin`/`admin`), NATS a token, and SurrealDB a root user. Passwords are
  generated per app and shown under 🔑 in the apps list, as with the other
  catalog credentials.
- **phpMyAdmin no longer sets `PMA_ARBITRARY=1`**, which turned it into a
  database client for any host the container could reach — a ready-made SSRF and
  lateral-movement tool for anyone who opened the page. It now points at the
  catalog's MySQL.
- **Apps that still cannot authenticate are labelled.** Memcached, Kafka,
  Prometheus, Netdata, Adminer, Cassandra, QuestDB, CockroachDB, Mosquitto,
  Elasticsearch, OpenSearch and SonarQube warn in the install dialog that
  anything able to reach their port can use them. Elasticsearch and OpenSearch
  keep their security layer off because bootstrapping it needs certificates and
  a password policy a one-click install cannot satisfy.
- **The scope model no longer lets a viewer read the host's secrets.** Database
  dumps, generated app credentials and `docker inspect` (which carries every
  container's environment) required only the `read` scope. Reads that return a
  secret are admin-only now, and per-container reads follow the same ownership
  rule as the actions. `inspect` masks environment values for anyone below admin.
- **Container `exec` is admin-only**, matching what the code comment and
  `SECURITY.md` already claimed. So are `install-runner` and `create-dockerfile`,
  which both reach host root, and backup **restore**, which wipes a live
  database.
- **The audit trail now records reads that return a secret and every denied
  request**, including failed logins, with the client address. Entries are
  hash-chained so an edit after the fact is detectable (**Verify chain** on the
  Audit page), the file is created `0600`, and rotation keeps five generations
  instead of one.
- **`--trusted-proxy` makes the login lockout and rate limiter work behind a
  reverse proxy.** Without it every request appears to come from the proxy, so
  one attacker's failed logins locked out every user. See
  [CONFIGURATION.md](docs/CONFIGURATION.md#--trusted-proxy).
- **The GitHub token and the generated app passwords are now encrypted on
  disk** (AES-256-GCM, key in `~/.config/pinqops/secret.key`). Existing
  plaintext files keep working and re-encrypt on the next change. This protects
  a config file that leaks *on its own* — a backup, a support bundle, a stale
  disk — not against someone who can already read the directory; set
  `PINQOPS_MASTER_PASSPHRASE` for that. **Back up `secret.key` with the config**,
  or restored secrets cannot be decrypted.
- **`pinqops update` now verifies what it downloads.** It checked only that the
  file was non-empty before chmod'ing it `755` and moving it over a binary that
  runs as root and talks to the Docker daemon. Releases publish `SHA256SUMS`
  plus a keyless cosign signature and an SBOM; the updater verifies the manifest
  and fails closed, keeping the current binary when it cannot.
- **The release workflow no longer interpolates its `version` input into a
  shell script** (it goes through the environment and must match `x.y.z`), and
  its permissions are least-privilege per job. A **CodeQL** workflow now runs on
  every push, pull request and weekly.
- **The stored GitHub token is only sent to `github.com`** unless you name a
  GitHub Enterprise host in `PINQOPS_GITHUB_HOST`. The API host was derived from
  the connected repository's URL, so setting a repository URL was enough to
  choose where the token went — and to make the dashboard issue authenticated
  requests to internal hosts. **GHES users must set this variable.**

### Changed

- **API tokens record who created them.** Who minted a token is not something
  that can be worked out afterwards, so it is captured now — tokens made before
  this have no creator, and empty is read as "unknown" rather than as anybody.
  It is there for the rule a token's team access should follow: derived from its
  creator's memberships, so removing someone from a team also removes the tokens
  they made. Until that lands a token's teams are only the ones it was explicitly
  added to, which is a deliberate act rather than an inheritance.

- **Ownership records left by the old shared token principal are labelled rather
  than guessed at.** Before each API token got its own identity they all shared
  one name, and the container-ownership records written then name nobody who can
  sign in today. They already resolve to unowned — which is admin-only, and
  therefore safe — but they look owned. They are now marked as stale so an admin
  can reassign the ones that still matter. They are deliberately not rewritten:
  deciding which of today's principals an old record "meant" would hand a
  container to whoever the guess landed on.

- **A bad proxy configuration can no longer be installed, and the proxy is now
  pinqops' own Caddy build.**

  The managed proxy runs with `--restart unless-stopped`. A Caddyfile it cannot
  parse was therefore never one failed reload — it was a container that crashes,
  restarts, crashes again, and takes every domain on the server down until
  someone edits a file over SSH. Every generated configuration is now checked by
  Caddy itself, in a throwaway container with no network and a read-only mount,
  *before* it replaces the live one; a configuration that would be refused is
  held back and the proxy keeps serving what it has. If Caddy accepts a file and
  then refuses to load it — a certificate it cannot obtain — the last file it did
  accept is put back, so a restart loop is not left armed for whenever the
  container next restarts.

  **Routes that get left out now say so.** The generator re-validates everything
  it emits, because `domains.json` is written by both the dashboard and the
  runner and read without validation — but until now anything that failed those
  checks was dropped in silence, leaving a route the dashboard listed as enabled
  and Caddy never served. What was skipped, and why, now comes back with the
  change and reaches the log.

  The dashboard and the runner each had their own copy of "save the config,
  re-render the Caddyfile, reload Caddy", and the copies had already drifted
  once — the runner's saved without re-rendering, so a preview route was recorded
  and advertised but never served. There is one implementation now, which is also
  what gives the runner the validation above. One behaviour follows from the
  merge: a preview deploy no longer issues a reload when no proxy is running.
  It used to do it blindly, which meant a failed `docker exec` on every preview
  deploy on a server without a proxy; the route is still written, and takes
  effect the moment one starts.

  The proxy image is now `ghcr.io/pinqponq/pinqops-caddy:2`, built by this
  repository's CI with xcaddy and signed with cosign. Stock Caddy ships no
  rate-limiting module at all, and every DNS provider is a separate module, so
  per-domain rate limits and wildcard certificates — which need a DNS-01
  challenge, because HTTP-01 cannot cover a wildcard — are not configuration
  away. A running proxy is unaffected until it is reinstalled, and because the
  data and config volumes are unchanged it keeps its existing certificates when
  it is.

- **One timed worker instead of one per feature.** Scheduled backups used to run
  from their own `BackgroundService`. The tick, the fire-and-forget and the
  "one broken thing must not stop the rest" handling were never specific to
  backups, so they now live in a single worker that asks each registered source
  once a minute what is due; deciding *what* is due stays with each feature.
  Backups behave exactly as before — the same enabled / not-already-running /
  window check, the same hourly, daily and weekly schedules, the same catch-up
  for a run missed while the host was asleep.

  Alongside it there is now a real cron parser, so a schedule can be written as
  `0 3 * * *` rather than picked from three fixed options. It handles ranges,
  lists, steps, month and weekday names, and the `@daily`-style macros, and it
  follows crontab's own rule that `0 0 13 * FRI` means every 13th *and* every
  Friday rather than only Friday the 13th. Quartz-only spellings (`L`, `W`,
  `#`, `?`) are refused with a message saying so instead of being quietly read
  as something else.

  Schedules are evaluated on the server's wall clock, which is what makes
  "03:00 every day" stay 03:00 across a daylight-saving change. Two consequences
  are deliberate and tested: a firing that lands in the hour the clocks skip
  forward is **skipped** rather than moved to a time nobody asked for, and one
  that lands in the hour they repeat fires **once** rather than twice.

  Nothing is scheduled with cron yet — this is the engine the Jobs page will
  run on.

- **Secrets are managed in one place: the container they belong to.** The
  compose `.env` editor no longer sits on the Deployments page. It opens from
  the key button on the container's own row under **Containers**, where the
  variables can be read in the first place — pinqops recognises a container of a
  connected app by its compose project label and shows that project's `.env`,
  with the file path and a line saying why the container's own environment is
  not the thing to edit (it is a copy taken when compose created it). Adding and
  removing values stays admin-only and **Apply** stays a deploy, each gated on
  its own scope now rather than both on admin. Deployments points at where the
  editor went.
- **Deployments and Runner moved out of Monitor and under Publishing**, below
  the GitHub connection that feeds them. Both are scoped to one connected app
  and both are empty until a repository is connected, so at the top of the nav
  they were the first thing a new install saw and the last thing it could use.
  Monitor is now Overview alone.
- **The dashboard says which app it is showing.** The per-app views act on one
  connected app, chosen silently — the first one, or whatever was last stored —
  and nothing on screen said which. Deployments now names it above the live
  card, and a picker appears in the top bar next to the server switch once a
  second app is connected.
- **Deployments and Runner lead with the answer, not the plumbing.** Deployments
  opens on a card saying what is live right now — the tag, when it went out, how
  it ended — with **Roll back to previous** next to it, followed by the deploy
  history. That table is down to tag / when / result, with duration, trigger and
  health check moved into a per-row **Details** dialog. Workflow runs and the
  compose `.env` editor now fold open below instead of stacking on the page, and
  the runs table is four columns wide instead of nine (workflow, branch, trigger
  and duration read as one grey line under the commit). Runner is a single
  sentence — online / offline, idle or busy, when it last ran a job — plus a
  **logs** button; the agent name, install directory, systemd units and the
  GitHub-side runner cards live behind **Technical details**.
- **The Environments page is now Settings → Servers**, and the host the
  dashboard runs on is called **Default**. Registering a second Docker host is a
  setting most installs never touch, so it no longer costs a nav entry; the
  "Add another server over SSH" form is folded away until it is wanted.
- **`PINQOPS_IMAGE` is now rejected by the dashboard `.env` editor**, alongside
  `PINQOPS_TAG`. Every deploy re-pins both, so a hand edit silently disappeared.
- **The dashboard auto-starts a stopped runner instead of asking for a click.**
  The deployment-readiness card now reads the runner's systemd state: an
  installed-but-stopped service is started automatically (idempotent), so the
  only thing a user runs is the GitHub wizard. When the service is running but
  GitHub still shows it offline — where a restart would not help — the row points
  at the runner's logs (usually a network/clock issue) instead of offering a
  "start" button that does nothing.
- **Split the "Storage & Networks" dashboard view into separate "Storage" and
  "Networks" tabs.** Storage keeps volumes and Docker disk usage; Networks holds
  the network list (create/remove/connect) and the visual network map. Each
  loads independently.
- **The GitHub view is now a single screen instead of a three-step wizard.** The
  connection, a repository **dropdown** (connected apps first, then the account's
  other repositories), and the publish panel are all visible at once — no stepper
  to walk through and no more duplicated "change repository" link plus repo name
  in the header. Choosing a connected app manages it; choosing a new repository
  connects and sets it up. Publishing is optional: connecting GitHub alone is a
  valid end state.
- **Removing an app is now discoverable and two-step.** The action moved out of
  the collapsed "Advanced" panel into a labelled danger zone under the repository,
  and its confirm dialog keeps the destructive button disabled until you type the
  repository's name back — no accidental removals.
- **Redesigned the notifications settings card.** Each channel (Webhook, Slack,
  Telegram) is now its own row with a brand logo, an on/off toggle switch, and its
  inputs; the trigger events are toggle pills grouped under a "Notify on" heading.
- **Sidebar navigation items are left-aligned.** Each entry's icon and label
  previously inherited a centering rule from the base button style, so the whole
  row floated toward the middle of the sidebar; they now hug the left edge.
- **Dashboard grids step down evenly.** The four-card stat rows on Overview and
  System go 4 → 2 → 1 columns and the paired chart rows 2 → 1 at the same
  breakpoints, so a row never ends with a single orphan card stretched across
  the leftover width and stacked rows share one column rhythm. The Docker
  disk-usage bars are one grid rather than one per row, so labels, tracks and
  sizes line up instead of each bar ending wherever its size text allowed.

### Fixed

- **A connected GitHub was reported as not connected.** "Is GitHub set up?" was
  answered by one flag that meant *a token is stored* **and** *at least one app
  exists*. Those are two different states with two different ways out, and the
  gap between them is exactly where a new operator stands: token pasted, first
  repository not yet published. Everything they could see insisted the step they
  had just completed was still undone — the overview banner, the empty app list
  on two pages, and the sidebar's padlock reading "Not connected — click to sign
  in" — and every one of them pointed back at the connect flow rather than at
  publishing a repository. Nothing failed and nothing was logged; the pages
  rendered as written, with the wrong sentence.

  The flag now answers the token question alone, and having an app is asked for
  separately where it is what actually matters. The half-configured state got
  the message it never had: *GitHub is connected — now publish a repository.*
- **The dashboard rendered nothing at all.** Two string literals were opened with a
  double quote and left to run onto the next line, which JavaScript does not allow.
  The page is one inline script, so the browser refused the whole of it: every handler
  undefined, every view empty, `Uncaught SyntaxError` and a blank product. Nothing in
  the suite read that file as code — the checks over it look for keys, handlers and
  links — so it shipped. There is now a check for the shape that got through, and a
  written note of the shape it cannot cover without a real parser.
- **`update` finished by reporting an assembly it could not load.** The binaries are
  self-contained single files, so every framework assembly is read out of the
  executable the first time it is needed — and the update replaces that executable
  while the process is still running. The warm-up written for exactly this built a
  process object and never started one, and it is *starting* a redirected child that
  pulls the pipe assemblies in. So the update installed the new binary, tried to read
  the new version back, and died on `System.IO.Pipes`: the operator was told the
  update had completed with an assembly-load error inside the same sentence, and
  `pinqops-ui update` left the service for them to restart by hand. Running it a
  second time worked, which is the tell — by then the new binary was the one on disk.
  The warm-up now starts a real redirected process before anything is replaced, and
  reports it when it cannot.
- **A host pinned read-only did not stop the container console.** Read-only is
  documented as refusing every change made through that host whoever is asking, and
  it does — for everything whose method says so. Opening a console is a WebSocket
  handshake, which is a `GET`, so it sailed past the refusal and every line typed
  into it ran on the host anyway. The one-shot form of the same capability, `exec`,
  is a write and was correctly refused, so the control held for one shape of the
  same act and failed for the other. The console family is now a change regardless
  of its method.
- **The container console opened on this machine whatever server was selected.**
  The socket address was assembled by hand and never named the selected host, so the
  server resolved it to the local daemon: the container list came from the remote
  host, the panel said so, and the shell was here. The Run button beside it went to
  the right place, so two controls in one panel addressed two machines — and the
  audit trail recorded the local one, so it did not contradict the operator either.
  The console now goes through the same helper every other container call uses.
- **A stack request could name another server and be served this one's.** Stacks
  are this machine's own files and its own daemon — nothing in that code can address
  a remote host — but the request was still authorized against the named host's
  grants, and then read the local stack's compose file and its `.env`, secrets
  included. A caller refused the local stack could be granted the remote one and get
  the local one anyway. Naming another server is now refused outright, the same way
  it already is for backups, deploys and domains.
- **Naming a host in the switcher disclosed the hosts other teams hold.** Grants on
  an environment are recorded against `local` — that is where the addressability
  check looks them up, and it is deliberately made before the selected host is set
  for exactly that reason. The listing looked them up under the selected host
  instead, so as soon as a request named one, every other team's environment came
  back unclaimed and was listed: name, transport, whether a key and a host key are
  stored. The same caller asking without naming a host correctly saw none of it.
- **A withdrawn secret lived on in every open preview.** Deleting a secret, or
  narrowing it to a different app, clears it from the app's `.env` — but a preview's
  `.env` was only ever merged into, never rewritten, so the revoked value stayed
  where the last deploy had put it and the next push handed it back to the preview
  container. The dashboard reported the secret as gone. A preview's `.env` is now
  replaced on each deploy, so a key production no longer has is dropped.
- **A preview joined the shared network instead of its app's own.** Publishing an
  app creates a network of its own so it cannot reach another app's database until
  somebody connects the two; the preview was put on the shared one, which holds
  every catalog service, every database and every legacy app. An unreviewed branch
  running with production's secrets could reach all of them while production
  deliberately could not. It also failed outright on a host that has no shared
  network — a host with an app and a runner but no proxy, which previews otherwise
  support. A preview now joins the network its application declares.
- **Rate limiting behind a CDN put every visitor in one bucket.** The limit was
  keyed on the immediate TCP peer, which naming your edge's networks does not
  change — so behind Cloudflare every request carried the same key, the edge node's
  own address, and one burst allowance was shared by everyone arriving through it.
  Ordinary visitors were refused before the app ever saw the request, and an abuser
  was indistinguishable from the crowd. This is the exact failure edge mode was added
  to fix. The key now resolves through the trusted networks; with no CDN configured
  it still resolves to the remote address, so an install without edge mode counts
  what it always counted.
- **Two domains could collapse to the same rate-limit zone and share one bucket.**
  The zone name replaced every character that is not a letter or digit with `_`, so
  `api-staging.example.com` and `api.staging.example.com` named the same limiter.
  Both are valid domains, both site blocks land in one file, and the limiter keys its
  state by that name — so traffic to one host spent the other's allowance and refused
  its visitors, with nothing in a position to notice, since each block is rendered on
  its own. A short digest of the domain is now part of the name.
- **The traffic access log was written where nothing could read it.** The proxy
  mounts the Caddyfile as a single read-only file and nothing else of its directory,
  so the log went into the container's own writable layer: the Traffic page read the
  host file, found none, and reported zero requests after any amount of real traffic
  — indistinguishable from nobody visiting. Every recreate of the container destroyed
  what had accumulated, and it filled the container rather than the host disk the
  roll settings were sized for. The proxy directory is now mounted and the log is
  written into it. Turning the log on still needs a republish for the mount to exist.
- **Turning the access log on did not write one.** The log directive was built from
  the setting and then dropped on the way into the site block, so the file the
  dashboard said it was writing was never mentioned to the proxy at all. That is the
  same zero on the Traffic page as the mount above, arrived at independently: one
  was the wrong destination, this was no instruction. Neither on its own produces a
  log; both are now right, and a domain with the log off still renders nothing.
- **A rule watching every container never sent the "resolved" after an outage.** A
  firing episode is meant to stay open across a gap — you were paged, the metric
  stopped arriving, it came back healthy, you are owed the all-clear. One tick after
  the series was written off as no-data it stopped being tracked at all, and the open
  episode went with it, so an incident opened by `alert_firing` never got its
  `alert_resolved` and stayed open for ever. A rule naming that same container by
  name resolved correctly, so the two shapes of rule disagreed about one event. A
  written-off series is now kept while its episode is open, and still drops out when
  there is nothing open to keep.
- **An alert batch stopped at one rule's share instead of using the whole budget.**
  The per-rule limit exists so one rule watching everything cannot crowd out every
  other rule on the host — but it was applied as an absolute ceiling in a single
  pass, so eight containers going down together paged five and abandoned three while
  fifteen of the twenty slots stood empty. The dropped ones were never reconsidered
  and, on the default "notify once", never paged at all; when they came back the
  all-clear was sent for an alert nobody had been told about, and it was always the
  same alphabetically-late containers. Every rule still gets its share before any
  rule gets more, and what is left over now fills the rest of the batch.
- **The alert history said "Sent" for alerts that went nowhere.** The trail was
  written from what the evaluator allowed, before anything was attempted, and the
  dispatcher's answer was thrown away — so on a server with no channels configured
  at all, a webhook that failed, or a rule pointing at a channel that is switched
  off, the operator's only record of whether they were paged said they were, and
  nothing was logged anywhere either. The trail is now written from what was actually
  delivered, and a batch that reached no channel says so in the log.
- **A catalog install that failed to start left a container nobody could clear.**
  `docker run` creates the container and then starts it, so a port already in use
  left one behind — but the ownership record is written only on success, and an
  unowned container is admin-only. The app showed as installed, Remove was refused
  to the person who had just installed it, and installing again failed on the name
  for ever. A failed install now removes the container it created, and only that one:
  a container that was already there when the install began is left alone.
- **Mongo Express could never connect to the MongoDB the catalog installs.** The
  MongoDB entry starts its server with a root password, so it requires
  authentication; Mongo Express was handed an address with no credential in it, and
  that address is the only thing it connects by. It was refused, failed at startup
  and restarted for ever, while the install reported success and offered a link that
  never loaded — and its own note had told the operator to install MongoDB first,
  which they had. It now carries the same cross-application password the WordPress
  entry already uses for MySQL.
- **De-registering a host left its access records for the next host of that name.**
  Grants and container ownership are keyed by host id, and an id is a name an operator
  chooses — `prod`, `staging`, the obvious one. Rebuilding a server and registering it
  under the name it had is the ordinary thing to do, and until now the old host's
  records survived it: the new machine's containers arrived already belonging to
  whoever held the old ones, with nobody having granted anything and nothing on any
  page that would say so. Deleting a team has taken its grants with it from the start,
  on exactly this reasoning; removing a host now does the same, and says in the log how
  many went.
- **A grant could be handed out and never taken back.** Revoking one named the host
  it was recorded against in `env` — the same parameter that tells pinqops which
  server to aim a request at. So revoking a grant on a host pinned read-only was
  refused as a change to that host, and revoking one on a host since de-registered was
  refused as unknown, while creating either had never been gated that way. The listing
  had the same collision, so such a grant could not even be found. Both now name the
  host the way the create side already did, in `environmentId`.
- **A container granted to a team listed for its members with no actions on it.**
  Two things decide whether an operator may act on a container: the ownership record
  that names them, and a manage grant held by a team they are in. The gate has
  consulted both since teams arrived; the map the page uses to decide which buttons to
  draw reported only the first. A container reachable solely through a grant therefore
  appeared with every action missing — the API would have allowed each one, and the
  page never offered it, so the grant looked applied and did nothing anybody could
  see. The map now reports what a grant allows as its own thing rather than by
  inventing an owner, so the actions appear while the owner column stays truthful, and
  a view grant still offers nothing to press.
- **The load table handed back every container the listing was hiding.** Filtering
  a container out of the listing stopped it appearing under Containers and nowhere
  else: the stats the dashboard fetches onto the same page were unfiltered and needed
  only the ordinary read scope, so the names another team had claimed came back
  anyway, with their CPU, memory, network and process counts beside them. It is
  filtered now, by the column `docker stats` actually uses for the name — the listing
  reports `Names`, plural and comma-joined, and reusing that reader here would have
  found nothing and emptied the table for everyone below admin instead.
- **A refusal read English in the page and Turkish in the toast.** The dashboard
  translates the server's prose when it arrives as an error, and not when the same
  sentence arrives as a field of a successful response. Two do: the Mail page's
  problem banner and the list of reasons an app cannot deploy without a gap. Both
  already had Turkish written for them, so an operator read the banner in English and
  then met the identical sentence in Turkish the moment they pressed Save.
- **One container's image verdict could land on another container's panel.** The
  "image up to date" check writes its answer into the open details panel without
  confirming the panel is still on the container it asked about, so opening a second
  container before the registry replies labelled the second one with the first one's
  verdict.
- **Switching servers mid-listing could delete the wrong machine's data.** The
  containers view drops a listing that arrives after the operator has changed hosts;
  Storage, Images, Apps, Networks and the overview did not. Their rows painted the
  previous host's contents under the new host's name, and a row action resolves its
  target by name against whichever server is selected at the moment it is pressed —
  so with catalog names being identical on every host, one click on Remove beside a
  stale `pinqops-postgres-data` deleted this machine's copy of it. All five now
  remember which server they asked and drop what comes back for another, and the
  check that pins it covers every listing rather than the one it was written for.
- **Changing the server's time zone did not reach the job scheduler.** Scheduled
  jobs run in the server's own zone — "3 a.m." means three in the morning where the
  box is — and .NET resolves that zone once and keeps it for the life of the process.
  Setting a new one moved the host, moved `docker logs` and `journalctl`, and moved
  the reading the Settings page shows back, but the scheduler went on firing in the
  old zone: the page said one thing and the jobs did another. The change then landed
  at the next restart, moving a nightly job by the offset with nobody having edited it
  and nothing in the log to say why. The process is now told to forget the zone it
  resolved, and only when the change actually took.
- **Turning deploys-without-a-gap off left the proxy pointed at the colour for
  good.** A colour's containers answer `<alias>-<colour>` and nothing else, and the
  last cutover pointed the routes there. Saving the setting did not move them, and the
  component that re-derives these routes at every restart skips an app that is no
  longer deploying in colours — so nothing ever corrected them. Every deploy from then
  on created containers on the plain alias, reported success, and was served to
  nobody, while the abandoned colour went on answering: production stayed on the
  release from before the toggle indefinitely, and the Copies page named an alias the
  routes were not using. Turning it off now brings the app up on its ordinary project
  first and then moves the routes to it, so there is no gap in the middle; if either
  half fails the setting goes back rather than leaving the silent state. The colour
  that was serving is left running — it is the operator's to stop.
- **Giving a host port back to an app that deploys without a gap could never
  succeed.** The two colours are two projects over one file and only one of them can
  bind a port, so taking the port back has the same prerequisite as running one copy
  — but only the copy count came down with it. The sequence then removed the network
  alias the colours are reached by and asked compose to bring one up, which cannot be
  worked out without that alias, so it failed at its last step every time and advised
  handing the port to the proxy: precisely what the operator had asked to undo. The
  rollback meant nothing broke, but the button could not be made to work. Turning the
  colours off now goes with the port, and comes back with it if the rollback runs.
  The three refusals this path can produce also reach a Turkish operator in Turkish
  now; they are written in the core, where the translation check does not look.
- **A release the proxy refused could install itself later anyway.** When Caddy would
  not accept a cutover, the deploy put the Caddyfile back, left the old colour serving
  and reported a failure — but the route change had already been written to the stored
  configuration, and that is what every later regeneration reads. The next domain
  added to any app, the next preview opening from the runner, the next apply from the
  Proxy page would install the switch that had been abandoned: production moved onto
  the release the operator was told had failed, with no deploy, no log line and no
  entry in the history, and the next restart moved it back again. A refused switch now
  puts the routes it changed back as well — only those, because another edit may have
  landed while the proxy was being asked.
- **The instant rollback moved the traffic and left the version behind.** Rolling
  back to the colour still running the previous release pointed the proxy at it and
  recorded it as active, but never changed what the project's `.env` says is running.
  Two things followed: the dashboard went on calling the rolled-back-from release
  "live now" and offering the one actually serving as the thing to roll back to, and
  the next ordinary compose action on the project — saving an environment variable, an
  autoscale tick — copied that stale pin into the live colour and recreated its
  containers on it. The operator changed a setting and the release they had just
  rolled back came back. The rollback now pins what it switched to, the way every
  other path that changes what is running already did.
- **A volume written one way was caught and the same volume written another way
  was not.** The gate that keeps a no-gap deploy away from a project-scoped volume
  read `appdata:` on its own line, but not `appdata: {}` or `appdata: null` — which
  mean exactly the same thing to compose. An operator who wrote it the second way
  was told the app was eligible, and the first switch started green on a brand-new
  empty volume: the application went live on an empty database, and the switch after
  that swapped back to data from before. Nothing warns about this and there is no way
  to notice until it is gone, which is why the gate exists. It now reads a volume
  however it is spelled, and a `volumes:` block written as a single mapping — which a
  text scan cannot read line by line — is refused rather than read as no volumes at
  all.
- **Running the tests rewrote the SSH config of whoever ran them.** Starting the
  dashboard rebuilds the pinqops-managed block of `~/.ssh/config` from the
  environment registry, and every test that boots the server did that against the
  real file of the person running the suite — leaving the fixtures' hosts aliased
  there, and a different set of them depending on which fixture happened to boot
  last. The path the sync writes is now injectable and the test host points it at a
  directory of its own; the dashboard still writes exactly the file it always did.
- **A pull-request preview could be handed another application's production
  secrets.** A preview is built from the pull request's image and given the
  application's production `.env` on purpose — that is what makes it behave like
  production — so the compose file it reads has to belong to the repository asking.
  It was never checked. Every connected repository's workflow runs on the same host
  under the same runner label, and the compose path comes from a repository variable
  that repository's own owner sets, so naming another application's file was a way to
  be handed that application's live credentials inside a container you control. The
  ordinary deploy has refused exactly this from the start, on the grounds that the
  project name is the owning repository's and is the only durable marker; the preview
  path now shares that refusal rather than having its own.
- **Turning a master passphrase on or off blamed the key file for it.** The two
  modes keep different things in the same file — a key when there is no passphrase,
  the salt for one when there is — so switching on an install that already holds
  secrets could not read it, and the refusal said the file "is not a valid pinqops
  key file". That sends an operator looking for corruption that is not there. It now
  says which way round the change was and what to do about it either way. The same
  collision made the suite fail at random, because the passphrase test set a
  process-wide variable and so changed the mode every test running beside it was in;
  it now passes the passphrase in directly.
- **Downloading a file from a volume saved the refusal instead of the file.** The
  Download link in the volume browser was an ordinary link, and nothing in pinqops
  authenticates by cookie — so the browser asked for the file with no credential at
  all, was refused, and because the link asked for a download it wrote the refusal to
  disk under a name taken from the address. There was no error in the page: the
  operator got a file that was an error message, and the feature had never worked. It
  now fetches the file the way the backup download does, and follows the selected
  server rather than always reading the local one. A test now refuses any link in the
  page that points straight at the API, because such a link can only ever save the
  refusal.
- **One log search read every collected file into memory before filtering.** The
  limit on a search bounded the answer and not the work: every watched container's
  whole archive was parsed first, then the first line was examined. At the ceiling the
  page itself advertises — twenty containers, four files each at 64 MB — a single
  request tried to allocate on the order of twenty gigabytes on the dashboard's own
  request thread. The read is now lazy the whole way down, including reading each
  file backwards in blocks rather than loading it to reverse it, so asking for twenty
  lines costs twenty lines. Measured: five lines out of a hundred thousand went from
  36 MB to under a megabyte.
- **A stopped container's last minute of log was collected six times over.**
  `docker logs --follow` exits when its container stops, and the collector starts it
  again ten seconds later reading a fixed one-minute window — so every restart
  re-read lines that had already been written, and appended them again. For a
  container that had just stopped, its final minute of output, which is exactly what
  someone is about to search for, was stored about six times; for one in a restart
  loop it never stopped. The duplicates are real bytes against the rotation limit, so
  older history was evicted that much sooner, and a search answered each line six
  times. A follower now resumes where the last one stopped — never reaching further
  back than the minute it always did, so this can only narrow the window.
- **Collected logs were never deleted, and the ones left behind stopped being
  counted.** Nothing in the collector removed a file. Rename a watched container — a
  redeploy under a new name, or an edit on the page — and its four files, up to a
  quarter of a gigabyte, stayed for the life of the server. Worse than the space: the
  usage figure walked the *configured* list, so those bytes stopped being counted the
  moment they stopped being wanted, and both numbers the page shows drifted further
  below the truth every time a name changed. For a feature whose entire safety story
  is a disk ceiling, that is the wrong direction to be wrong in. Usage now counts
  what is actually on disk, and a container taken off the list has its files removed.
  Switching collection off and the low-disk pause deliberately delete nothing — the
  first is a pause an operator expects to find their history after, and the second
  fires at the one moment that history is most likely to be wanted.
- **A docker failure made the recovery page say there was nothing to recover from.**
  The point-in-time archive lives in docker volumes the dashboard cannot open
  directly, so every read of it runs a throwaway container — and a read that failed
  was reported as an archive with nothing in it. The page then showed no window and
  no base backups, and a recovery was refused with "there is no base backup to
  recover from. Take one first", which is advice to destroy the window the operator
  was trying to recover into. The command's own shell already swallows its errors, so
  a failure can only mean the read did not happen; it now says so.
- **A volume that could not be read was shown as an empty one.** Browsing a volume
  runs a throwaway container over it, and every failure of that container except
  "no such path" was treated as "there is nothing here" — so on a host where the
  helper image is not cached and the registry is unreachable, or where the daemon
  went away between the listing and the browse, a volume full of data rendered as
  "Nothing here." The next thing anyone does about a volume they believe is stale is
  remove it. Only the genuinely silent failure — which is what an empty directory
  produces — is still read that way; anything that says why it failed now says so.
- **Restoring a PostgreSQL backup reported success even when nothing applied.** psql
  prints every statement error and still exits 0, and the exit code is all the
  restore reads — so a dump that was cut short, half-copied, or that conflicted with
  what the cluster already held was reported as a completed restore, at the one
  moment an operator is relying on that being true. The database *upgrade* path
  learned this and was fixed; the backup restore was missed. The other three engines
  exit non-zero on the first error, so postgres was the only one where success meant
  nothing.
- **A deleted user's team memberships stayed behind, and the next person with that
  name inherited them.** A membership is recorded by username, and a username goes
  back into the pool the moment the account is gone — so removing someone left a row
  in the team directory pointing at a name anybody could take next. An invitee given
  no team at all could accept an invitation under that name and become a member of
  every team the removed user belonged to, holding every grant those teams hold.
  Deleting an account now clears its memberships in the same request. Demotion
  deliberately does not: a demoted user still has their account and their teams, they
  simply have less scope.
- **An API token outlived the account that made it.** Removing someone, or lowering
  their role, ended their browser sessions and stopped there. A token is a second key
  the same person holds — it can be minted at admin scope with no expiry, and it
  authenticates as a principal with no account behind it — so losing the account
  touched nothing about it. Someone removed from the server kept indefinite admin
  over the whole API, including resetting any other user's password and stripping
  any other user's second factor. Both actions now withdraw the tokens that person
  minted along with their sessions. A token from before pinqops recorded who created
  it belongs to nobody knowable and is left alone, which is the same rule the rest of
  the product applies to that field.
- **A second factor could be guessed off an account.** Signing in with a code has
  always been throttled, because six digits is a million combinations and three of
  them are current at any moment. The two routes that verify a code *outside* sign-in
  — turning the second factor off, and replacing the recovery codes — were not
  throttled at all: a wrong code cost nothing, and a session token is not tied to an
  address, so the per-address request limit was not a ceiling either. Someone with a
  borrowed session, which is the exact case the disable route exists to guard
  against, could work through the space and strip the protection off the account.
  Replacing the recovery codes was worse per guess, because each wrong one still had
  the server hash ten new codes. Both now share the sign-in step's ceiling.
- **A scheduled job that could not be started disappeared instead of failing.** A
  timeout was handled and a non-zero exit was handled, but a command that could not
  be launched at all — docker missing from the path, not executable, refused by the
  OS — threw straight out of the job runner. That skipped the retries, skipped the
  history entry and skipped the failure notification, which is the one thing whose
  purpose is to say a job failed. The Jobs page went on showing the job as enabled
  with no runs at all, which reads exactly like "not due yet", so a nightly dump
  could stop happening indefinitely with nothing to notice. A failed launch is now a
  failed attempt like any other, and the reason is kept with the run.
- **The Traffic page could hang the server on a busy proxy.** The summary reads the
  most recent 200,000 access-log lines, and dropped the oldest one by shifting the
  entire window along — so every line past the cap cost a copy of all 200,000. The
  cap exists precisely so the page cannot get slower as traffic grows, and it was
  what made it: measured, a window holding 600,000 requests took 15 seconds and one
  holding a million took 32, all of it on the dashboard's own request thread.
  Dropping the oldest entry is now free, and the same read takes milliseconds.
- **Two dashboard views could paint one thing's data under another's name.**
  Switching servers while the container listing was in flight filled the table with
  the previous server's containers under the new server's name — and every row
  action resolves its target against whichever server is selected now, so acting on
  one of those rows hit the wrong machine. Opening a second container before the
  first one's details came back showed one container's data under the other's
  title. Both now check, at the moment of painting, that what was asked for is still
  what is wanted. The refresh behind the details dialog had the same gap and got the
  same check.
- **Giving a port back to an app could leave it published by nothing.** There is no
  way to hand a listening socket from one container to another, so the proxy has to
  let the port go before the app can bind it. Enrolling has always been wrapped in a
  rollback for exactly that reason; unenrolling was not, and it ran the step that
  can actually fail — starting the app's own container — after the proxy had already
  given the port up. The app was then published by neither, and the dashboard said
  the change had worked. It now puts the app back on the proxy and says what failed.
- **A deploy in flight undid settings changed while it ran.** A no-gap release
  reads the app's deploy settings before it pulls anything and writes them back
  minutes later when traffic moves. It wrote the whole file, so anything changed in
  between — the copy count, autoscaling, the colour settings themselves — was
  quietly put back the way it had been. The cutover now changes the one thing it
  means to change. The store's lock had read as though it prevented this; it could
  not, because it was an instance field on a class every caller creates its own copy
  of, so no two callers ever held the same one. It is now shared per file.
- **Two proxy changes at once could validate each other's file.** Every Caddyfile
  was checked by writing it to one fixed path and asking Caddy to parse it. Adding
  a domain, a deploy's cutover and the proxy watchdog all apply, and nothing stops
  two of them overlapping — so the second writer replaced the first one's file and
  the first one's cleanup deleted the second's. One check then found no file, and
  the other passed a config it had never written. That second half is what mattered:
  a Caddyfile that would have been rejected could be installed because it was
  checked against somebody else's, and the proxy restarts in a loop on one it cannot
  parse. Each validation now writes a file of its own.
- **A failed no-gap release left the project naming a version that never ran.** A
  coloured deploy pins the image and tag into the project's shared `.env` before
  it pulls anything, and that file is not a record — it is what every ordinary
  compose action on the project reads, and the answer to "what version is this
  running". None of the four ways the release can end early put it back, so a
  failure anywhere after the pin left the file describing a version that never
  served a request while the containers went on running the old one: the next
  restart or scale change would have started the version that had just failed. The
  colour-blind deploy path has always restored it; this one now does too, on every
  early exit.
- **The mail relay's greeting name could carry a second SMTP command.** The name
  pinqops gives in `EHLO` goes into the command as typed, so a carriage return in
  it ended that line and made whatever followed a command of its own in the same
  session. The two settings beside it on the form — the sender name and the relay
  username — are already refused for exactly this; this one was not checked
  anywhere. It is now refused rather than quietly cleaned up, so the relay is never
  greeted by a name other than the one on the page.
- **Rotating a secret could file it under an app that does not exist.** Creating a
  secret checks that its scope is either global or an app you have actually
  connected, because an app-scoped secret only ever reaches a container by way of
  that app's `.env` — one filed under an unknown id is stored, listed and revealed
  while never reaching anything. Rotation writes through the same call, which
  creates the secret when none is there, and it did not check. So the scope check
  could simply be walked around by rotating instead of creating. Both routes now
  apply it.
- **Preview environments ignored who an app was granted to.** Manual teardown is
  addressed by app id in the path rather than the query, so it was the one such
  route that resolved the app itself instead of going through the shared request
  helper — and the helper is where the visibility rule lives. The rule is an
  optional argument, so leaving it out compiled and read like every other call
  while quietly keeping the behaviour from before teams existed. Anyone with the
  deployer role could therefore destroy the preview containers, volumes and
  compose project of an app granted exclusively to another team — an app the
  settings page will not even list for them. The preview listing had the same gap
  in the other direction: it enumerated every app's previews for every caller.

  Both now filter. So that the gap cannot return under another name, a test
  asserts that exactly one file in the product resolves an app by hand.
- **Handing an app's port to the proxy could never work.** The rewrite commented
  out the port mapping and renamed the `ports:` key to `expose:` — without listing
  anything under it. A key with only comments beneath it has no value, and compose
  refuses the whole file, so the change was written, rejected and rolled back
  every time. Because replicas, autoscaling and no-gap releases all require an app
  whose port the proxy holds, none of those three could be turned on for any
  generated project either. The rewrite now leaves a real entry behind.

  Two more faults in the same rewrite. It converted the *first* `ports:` in the
  file, which is the wrong service whenever another is declared above the app —
  so enrolling one app unpublished another. And undoing it rewrote *every*
  `expose:` in the project into `ports:`, so a service that had always been
  internal-only — a database reachable by name and nothing else — started
  publishing on the host. Both now act only on the app's own service.

  The tests asserted that the text "expose:" appeared, which the broken output
  contains as readily as the correct one, and a second fixture had copied the
  invalid shape in as "the shape pinqops generates". They now check that something
  is listed under the key, and that a sibling service is left alone in both
  directions.
- **The most common refusal in the product was never translated.** "Your role or
  token does not grant the scope this action requires" is what a Turkish reader
  got, in English, every time they were refused — while a message for a check
  that nothing calls any more sat translated beside it. The rejection shown when
  an alert channel's URL is malformed was untranslated too, and arrived with
  `(Parameter 'url')` stuck on the end: a note for whoever wrote the method,
  shown to whoever typed the URL.

  Both are translated now, and the parameter note is gone. The reason the
  existing guard missed them is recorded with the new one: it scans exceptions
  thrown from the web project, so a message written straight into a response body
  and a message that comes from the shared logic are both outside it.
- **A Dockerfile in a subdirectory was never found again.** The wizard offers the
  Dockerfile locations a monorepo produces, commits into whichever one is picked,
  and records the directory on the repository so the workflow builds from it. The
  workflow does. The dashboard went on reading the repository root, which for such
  a project means finding nothing at all — so the container port was seeded from
  the fallback instead of the `EXPOSE` just written, and the app was published on
  a port its image does not listen on while the wizard reported success. The
  Dockerfile step also stayed marked as missing, and generating one a second time
  failed because the file the dashboard could not see already existed.

  It now looks where the workflow builds from, resolving the path the same way
  the workflow does. A token that cannot read the repository's variables falls
  back to the root rather than breaking the page — the answer decides where to
  look, not what to do.
- **Rolling back a no-gap release started a third copy and called it done.** The
  fast rollback points the proxy at the colour still running the wanted version,
  and when no colour is, it falls back to a full redeploy. That fallback was
  colour-blind: it ran `compose up` with no project name, which against a
  two-colour project starts a *third* copy — one nothing routes to, holding the
  same volumes — and then reported the rollback as successful while the proxy
  still pointed where it always had. Both the command line and the dashboard did
  this.

  The fallback is now itself coloured, so the rollback actually happens. And the
  colour-blind deployer refuses a two-colour project outright rather than relying
  on every caller to remember — the compose editor already documented this exact
  danger in its own code, and both rollback paths walked into it anyway.
- **A no-gap release left no trace that it happened.** Every ordinary deploy
  writes an entry in the app's deploy history and hands the result to whichever
  notification channels are configured. A coloured one did neither — the cutover
  sequence takes no history store and no observer, and the deploy returned as
  soon as it finished. So turning a project's colours on switched off its deploy
  history and its notifications at the same time, silently, for the one kind of
  release where knowing it happened matters most. Both are recorded now.

  A failure is recorded with what went wrong but *not* as a failed health check:
  a coloured deploy can also fail at the pull, the eligibility gate or the proxy
  reload, and the result does not say which — naming a specific reason that is
  often the wrong one is worse than recording none.
- **An app with no runner was shown another app's runner.** Finding the service
  behind a runner directory falls back to scanning the host's runner services,
  filtered to the repository that directory belongs to. A directory that has no
  runner yet belongs to no repository — and having nothing to filter by was
  treated as matching everything rather than nothing, so the scan returned
  whichever runner was listed first.

  On a host with more than one app that is an ordinary state: an app added but
  not yet given a runner had another app's service, and its running or stopped
  chip, shown as its own. Nothing to filter by now means no match.
- **An upgraded database came back on the wrong network.** During an upgrade the
  replacement container publishes no ports on purpose — the old one is still
  holding them and still serving — so the only way to the new one is by name on
  the shared network. It was never put on that network: it landed on docker's
  default bridge, publishing nothing and answering to nothing. The upgrade then
  reported success, and every app that used the database could no longer reach
  it. It is now created on the same network every other catalog container is.
- **A leftover SSH entry could override the pinned host key.** OpenSSH uses the
  first value it finds for each setting, and pinqops wrote its block at the end of
  `~/.ssh/config` — so any earlier entry for the same alias quietly won. That
  includes the two settings the whole remote-host story rests on: which key is
  offered, and which host key is trusted. It is also not hypothetical, because
  when a marker comment goes missing pinqops deliberately keeps the operator's
  lines rather than truncating their config, and those lines include the orphaned
  entry it is replacing — so the fresh block was written behind the stale one.

  The block now goes at the top, where what it says is what takes effect. It ends
  on a catch-all entry so that a config which opens with settings belonging to no
  particular host keeps applying them to every host, exactly as before.
- **Changing a password and removing a second factor left no audit line.** The
  audit trail decided what to record by asking the authorization layer what scope
  a route needs — and the handful of routes anyone may call for their own account
  are deliberately classified at the lowest scope, so a viewer can protect their
  own login. Two different questions with one answer: lowering a route's scope
  silently stopped it being recorded.

  What that left out is the part worth having. Taking a second factor off someone
  else's account is the admin break-glass; changing a password is the other way an
  account changes hands. Both are now recorded, along with every other write,
  while ordinary reads stay out of the log as before.
- **Giving a team an environment hid it without taking it away.** The grant
  filtered the environment out of everyone else's switcher and stopped there —
  the host itself stayed fully addressable, so anyone who knew the id could name
  it and operate on it. That is the wrong way round: the row in the picker is the
  least of what granting a server is meant to control. Naming an environment you
  have not been granted is now refused, in the same words as naming one that does
  not exist, so the refusal does not tell you which it was.

  A request that names no environment is unaffected. That one is asking for this
  server's own daemon, which everything runs on by default — refusing it would
  lock someone out of the product rather than out of a host, which is not what
  granting a remote environment means.
- **A PostgreSQL upgrade could report success over an empty database.** The
  restore step reads the client's exit code as "the data is in", and `psql` does
  not co-operate: it prints every statement error and still exits 0. So a restore
  in which nothing at all applied was indistinguishable from one that worked — at
  the one point in the upgrade where the old container has already been dumped
  and the new one built. It now stops at the first error, so the exit code means
  what the caller reads it as. MySQL and MongoDB already behaved this way; that
  is now asserted rather than assumed.
- **An app with a long repository name could never hold a secret.** An app's id
  is its owner and repository joined together, with no limit on how long that
  gets — but binding a secret to one app imposed a limit of its own, on the
  stated grounds that an app id is "already constrained", which constrains the
  characters and says nothing about the length. GitHub allows 39 characters of
  owner and 100 of repository, so this was an ordinary repository rather than a
  contrived one, and the affected app silently received no secret ever, under a
  name its operator never chose and could not change. The limit now covers any id
  that can be minted, and the two are tied together by a test so raising one
  without the other fails.
- **Every cron mistake was explained in English only.** Getting a scheduled job's
  expression wrong is the most likely way to get one wrong, and all nine ways the
  parser can say so reached Turkish readers untranslated — as did the complaint
  about an image reference, which had been written two different ways so the
  translation that existed matched neither. The wording is now one wording, and
  the nine are translated.

  These messages come from the shared logic rather than from the web layer, and
  the test that guards translations only reads the web layer — so a message that
  originates one layer down and is handed to the operator by an endpoint sits
  outside its net. Worth knowing about the guard rather than assuming it covers
  everything an operator can read.
- **One odd registry could stop the update check for every image.** Asking a
  registry for a token assumed a successful answer would be JSON, and a 2xx is no
  promise of that — a captive portal, a proxy interstitial or a load balancer's
  error page all answer 200 with HTML, and a registry that replies with a JSON
  array rather than an object got just as far. Either one threw out of a method
  whose whole job is to hand back a reason instead, and because this runs on the
  hourly check for new images, one such registry took the check down for all of
  them rather than failing its own lookup. Both now come back as "no token", the
  same as a reply that carries none.
- **The Logs page understated its own disk ceiling by a quarter.** The figure
  counted the rotated copies of each log but not the one being written, so the
  worst case it promised was three files per container where four exist — in the
  same answer that reports the bytes already used, counted over all four. A
  ceiling the usage beside it can exceed is worse than none: it says the guard is
  holding while the number next to it says otherwise. The sum is now checked
  against what the rotator actually leaves on disk, so the two cannot drift again.
- **The job history showed "[object Object]" instead of how long a run took.**
  The cell was built with the wrong key — `t`, which everywhere else in the page
  is the translation function — so the table had nothing to render and fell back
  to printing the wrapper.
- **Four pages were showing another page's words.** Eight labels were defined
  twice, and a repeated definition is not an error in the page — the later one
  simply wins. So Buckets quietly took four labels from Backups, Secrets took two
  from Copies, the log filter's placeholder took the Search heading, and the DNS
  resolution message took the provider list's "none". Each of the four older
  features had been displaying the newer one's text ever since.

  The one that mattered was the Backups "remove schedule" dialog, which had
  become the wording for deleting a storage bucket — different wording, on a
  destructive action, with a placeholder nothing filled in. The test meant to
  guard the labels compares the two languages as sets, so a key duplicated in
  both looked perfectly matched; it now also refuses a key defined twice.
- **An offsite folder written with a leading slash lost track of its backups.**
  Uploads normalise the folder you configure — `/prod` and `prod` write to the
  same place — but the listing asked for the spelling you typed. So with a
  leading slash, every upload succeeded and every listing came back empty, which
  S3 reports as a perfectly good answer rather than an error. Nothing said
  anything: retention found no copies to remove, so the bucket grew past its
  configured count indefinitely, and the page that offers those copies back — the
  one that matters when the server itself is gone — showed none. The listing now
  asks for the folder the uploads actually used, because it is built by the same
  code that puts them there.
- **Mistyping a code while turning two-factor off signed you out.** Removing your
  own second factor needs a current code, so that a borrowed session cannot strip
  the protection off the account it is signed in to. Getting that code wrong
  answered "you are not signed in" — and the dashboard believes that answer, so a
  typo dropped the operator to the lock screen and threw away both the message
  and the page they were on. The caller is signed in; it is the code that was
  refused, and that is what it now says.
- **A new scheduled job ran immediately, whatever time it was set for.** Asking
  the run history when a job last ran answered with the earliest date there is
  when it had never run at all — which is not "never", it is a last run two
  millennia overdue. So a job saved with `0 3 * * *` at noon ran at noon, and
  again on the next tick until its history caught up. Saving a job now means
  scheduling it, which is what writing the expression asked for. Backups already
  drew the distinction correctly; jobs now do it the same way.
- **Turning log collection off did not stop it.** The collector started followers
  and never stopped one. So switching collection off, taking a container off the
  list, or dropping below the free-disk floor all left every `docker logs`
  process running and writing, until the dashboard itself was restarted.

  The disk floor is the worst of the three: it exists precisely because a full
  disk stops the database, the proxy and the deploy, and it was the one control
  guaranteed to fire exactly when it could no longer do anything. Each of the
  three now stops the followers it should, and which followers should be running
  is decided in one place rather than through three separate early exits.
- **Docker volumes could no longer be added as a backup target.** Every new
  target was checked against the list of database dump commands, and a volume has
  none — it is archived whole rather than dumped by a database client. So picking
  a volume and clicking Add was refused outright, even though backing one up,
  restoring it and naming its snapshots all worked. Half the Backups picker was
  unusable. The check now applies only to the targets it is about.

  It also had no test: the two that mention volumes both write the target
  straight into the config file, so they covered everything after the point where
  this broke and nothing at it. Adding a target now goes through the same route
  the page posts to.
- **An invitee could claim the name that owns the oldest containers.** Every API
  token used to sign in as one shared principal, `api-token`, and the container
  ownership records written back then still name it. Those records are safe for
  exactly one reason: nobody can sign in as that principal any more, so they
  count as unowned, which is admin-only. It is also why the move to teams
  deliberately left them alone rather than turning them into grants —
  reinterpreting them would hand access to whoever the guess landed on.

  Nothing stopped an account being created with that name, and the route where
  that mattered is the one nobody has signed in on yet: accepting an invitation,
  where the invitee picks their own name and need not have been invited as an
  admin. Reserved names are now refused. The two routes that take a name from a
  caller had a copy of the rule each, which is how one could have been fixed and
  the other missed; they now share one.
- **A vault entry named with a dash crashed whatever asked for it.** The vault
  accepts letters, digits and underscores, so `s3-secret` is a name it refuses
  outright rather than one it has not got — and the code that reads secrets
  guarded only against the entry being *missing*. Everywhere that mattered, the
  refusal escaped as an unhandled error: a scheduled offsite backup died instead
  of recording why it could not run, a registry sign-in and the hourly image
  update check the same. Each of them now reports it, the way each already
  reported a missing entry.
- **39 Turkish error messages showed a literal "$1" where the value belonged.**
  A translation written as plain text carries the values the English message
  captured — the container name, the port, the count — as `$1` and `$2`, and the
  page handed it over without ever putting them in. So a Turkish reader was told
  that `'$1'` is not a file in `$2`, which is worse than the untranslated English
  would have been: it names nothing at all.

  The test that was supposed to guard this checked that a Turkish translation
  *exists* for every message, which was true throughout. It now also checks that
  a translation with placeholders actually expands them, and that no placeholder
  asks for a value its own pattern does not capture.
- **Wildcard domains were refused even with DNS-01 set up.** Adding a domain
  validates the name with wildcards allowed or not depending on whether a DNS
  provider is configured — and then handed the accepted name to the DNS preflight,
  which validated it a second time with wildcards *always* refused. So
  `*.example.com` was rejected with "an HTTP-01 certificate cannot cover them" on
  exactly the setup that exists to make it work. The preflight now applies the
  same rule as the route that calls it.

  It also no longer looks a wildcard up. A wildcard has no address of its own, so
  the lookup could only come back empty and be reported as "this domain does not
  point at this server" — a red warning about nothing. The page now says what a
  wildcard actually needs, including the part this check cannot answer for: the
  names underneath it still have to point here.
- **Autoscaling never once fired.** The controller looked for an app's containers
  by its id — `acme-shop` — while compose names them after the project, which is
  the repository alone: `shop-app-1`. Nothing ever matched, so every tick read no
  load, and no load is not "idle" — it is a controller with nothing to decide
  from, which returns "leave it alone" forever. It did that silently, while the
  Copies page reported autoscaling as on. It now reads the containers of the
  app's actual compose project, including a coloured deploy's.

  The reason this survived is worth recording: the scaling rules were thoroughly
  tested, but only by handing the decision its readings directly. The step that
  produces those readings in production had no test at all, so a feature that
  could not work looked well covered. That step is now tested.
- **A bad DNS secret could leave the server with no proxy at all.** Installing or
  republishing the proxy removed the running container first and only then read
  the DNS-01 secret out of the vault — so a secret that had been deleted, or
  renamed to something the vault will not accept, failed at the point where there
  was nothing left serving. Every domain on the server went down for what should
  have been a refused install. Both paths now read the secret before they touch
  the running container: acquire, then commit.

  The name check the DNS settings carry only asks that *something* was typed, so
  a perfectly natural `cf-token` — the vault takes letters, digits and
  underscores — got past it and then failed as an unhandled error rather than as
  an answer. It now says what is wrong with the name.
- **Saving the copy count took a blue-green app off the air.** Under blue-green
  the running containers answer on a colour-qualified network name and on nothing
  else — that qualification is exactly what stops two versions from splitting
  traffic. The Copies page did not know it: saving three copies pointed the
  routes at the unqualified name, which resolves to no container, and saving one
  cleared the replica set entirely, falling back to a container name no colour
  ever creates. Either way every request to the app failed, until the next full
  deploy or a dashboard restart — because the reconciler that would have put it
  right only runs at startup. Saving a copy count now keeps the colour, and the
  page quotes the name the routes actually resolve rather than the one in the
  app's `.env`.
- **The container console checked one host and opened a shell on another.** With
  a remote environment selected, the console was authorized against *that*
  environment's ownership records and grants, and then ran `docker exec` against
  the local daemon — so the check and the shell were on different machines, and
  the audit line named the host it had not used. This is the failure the
  environment layer exists to prevent, stated in its own comment as "the caller
  believes it stopped a container on a staging box and it stopped one in
  production" — with a shell rather than a stop. The console now runs on the
  daemon the request selected, the same one the gate consulted, and records it.
- **The container console was open to anyone who could reach the container.**
  Running a single command in a container (`exec`) is deliberately admin-only.
  The console is that same thing held open — but it is the one route where the
  method says nothing about what it does, because everything else that runs code
  is a write while opening a socket is a GET. It was never classified, so it fell
  to the coarse read default, and the per-container gate that follows admits
  anyone who *manages* the container — which a deployer does for any container
  marked public. The result was a caller refused one command and handed an
  interactive shell instead. The console is now admin-only, like `exec`.
- **A dashboard read landing on a settings save could throw the save away.**
  Every config, credential and state file in pinqops is committed the same way —
  written to a sibling temp file, then renamed over the target — and replacing a
  file requires delete access to it, which a concurrent reader opening it the
  ordinary way withholds. A GET arriving inside the microseconds a write needs
  therefore made the *write* fail: the operator's change was discarded and an
  error surfaced, for nothing worse than the browser's own auto-refresh landing
  at the wrong moment. Both sides now pass instead of colliding — the commit
  waits a reader out on a bounded budget, and reads no longer hold the file in a
  way that blocks a commit.
- **The build no longer emits a warning.** A database version upgrade leaned on
  an invariant the compiler could not see — that the upgrade check has already
  refused an engine pinqops does not recognise — and asserted it with a bare `!`
  at each use instead of stating it once. No behaviour changes; the state the
  warning pointed at was already unreachable.
- **Adding a route could be lost the same way, and had its own copy of the bug.**
  The proxy's routing table — every domain and published port the proxy serves —
  was the one store not written through the shared primitive. Its private copy
  of the atomic-write logic retried the wrong failure, so it did not survive the
  overlap above, and it set the file's owner-only permissions after creating it
  rather than before. It now goes through the same path as everything else.
- **The self-updater's verification was not actually being tested off Linux.**
  Only linux-x64 binaries are published, so the updater declines to swap
  anything in anywhere else — correctly, and it still does. But that refusal
  came before everything worth checking, so on any other machine the tests for
  the parts that decide whether a binary running as root gets installed
  (checksum mismatch keeps the current one, a manifest that doesn't list the
  asset keeps it, the manifest is fetched before the swap) either failed for the
  wrong reason or *passed* for the wrong reason — "the original was kept" holds
  trivially when nothing was ever downloaded. Those tests now exercise the real
  path on every host, with the platform refusal covered by a test of its own.
- **A background tick or an API request could die on a store read.** The other
  half of the same race: a read that started while a write was committing failed
  outright, and because every store's `Load` catches malformed JSON and nothing
  else, that failure escaped and took down whatever had called it — an alert
  evaluation tick, or the request being served. Store reads now ride out a
  commit happening underneath them. A file that simply isn't there yet still
  fails immediately, as it should — that is every store's first run, not
  contention.
  `{data, redacted}`, and the dialog read `Config.Env` off the envelope rather
  than off `data` — so the lookup missed for every container and each one
  rendered as "This container has no environment variables."
- **Dashboard (embedded UI), a batch of ten.** The boot-failure card's Retry
  button did nothing — its inline `onclick` is exactly what the page's CSP
  forbids; it is now wired from the hashed script. Editing an alert rule whose
  `for` window was set through the API to a non-preset value silently rewrote
  it to 5 minutes on save — the window now gets an option of its own, as the
  repeat interval already did. Closing the container-detail modal left the
  Stats tab's 2-second poller running forever against `/api/docker/stats`.
  With a remote environment selected, the Domains and Backups pickers listed
  the *remote* host's apps and volumes for features that run only locally —
  scheduling a backup there dumped same-named local data while presenting it
  as the remote's; both pickers are now pinned to the local environment.
  The bulk-actions "remove" button and "+ Add container" were offered to
  deploy-scoped users whose every call would 403 — both are admin-gated now,
  like the row-level remove always was. The detail Overview's owner row was
  HTML-escaped twice, showing `o&#39;brien` for `o'brien`. "Duplicate / edit"
  flattened the container's command with a plain space-join, so an argument
  containing spaces (`sh -c "nginx -g 'daemon off;'"`, a password with a
  space) re-split into a broken argv in the copy — arguments are now quoted
  for the tokenizer. A failed host-port availability check left Publish
  disabled with no error shown. The Alerts view toasted an error every
  auto-refresh tick while the server was restarting (and on top of the lock
  screen after session expiry), defeating the quiet-tick suppression every
  other view honors. And the Containers view linkified loopback-only
  published ports as `http://<host>:<port>` links that can never connect —
  they render as plain text now, matching the Apps view's "local only" chip.
- **A credentials file holding both a legacy and a migrated key broke every
  catalog install.** The legacy-key migration in the credential store built its
  map with `ToDictionary`, which throws on the duplicate that arises when a
  bare `redis` entry (written by a pre-environments build) coexists with
  `local/redis` — and the store's catch only covered bad JSON, so every
  credential read and install failed until the file was hand-edited. The map
  is now built entry by entry with the namespaced record winning, exactly as
  the ownership store already did for the identical hazard.
- **A healthcheck command line escaped secret redaction.** The inspect payload
  masks every argv it knows (`Cmd`, `Args`, `Entrypoint`, `Path`, `Command`)
  because catalog apps put generated passwords straight into command lines —
  but `Config.Healthcheck.Test` is an argv too, and `redis-cli -a <password>
  ping` is the canonical healthcheck. It is now masked wholesale like the
  others, so a deploy-scoped caller can no longer read a password out of a
  container's healthcheck.
- **Deleting the shared `admin` account could lock the sole remaining user out
  of the UI.** With exactly one account the lock screen hides the username
  field and the login maps a missing username to the legacy `admin` — which no
  longer exists once an operator created a personal account and deleted the
  shared one, so every UI login answered "Wrong username or password" forever.
  A missing username now means the sole account when only one exists, and the
  legacy admin otherwise, so old clients keep working.
- **Two concurrent admins could remove or demote each other and leave the
  dashboard with no admin at all.** The "cannot remove/demote the last admin"
  guard (and the duplicate-username check on create) counted a snapshot taken
  outside the store's serialized update, so both requests saw two admins, both
  passed, and both mutations then applied. The lookups and guards now run
  inside the update, under its lock.
- **A request naming a remote environment could silently operate on the local
  server.** Deploys, rollbacks, compose edits, domains, proxy, previews,
  settings and setup are all hard-wired to local singletons, but the
  environment gate accepted `?env=prod` on them — the action ran here while
  the audit trail recorded it against the remote. They now refuse a non-local
  environment outright, as backups already did.
- **A second environment registered with local transport split the ownership,
  credential and job key space for the host.** `Env()` short-circuited local
  environments to the DI-default service whose id is `local`, while every
  gate and store keyed by the request's environment id — so an install under
  `?env=twin` wrote records the reads never found, credentials came back
  empty, and the installer's own uninstall was refused. Local environments now
  route through the same endpoint mapping as SSH ones, keeping their id.
- **A muted, still-firing alert wrote one trail entry instead of one per repeat
  interval.** The suppressed-transition mark that the trail's "worth another
  entry" check measures from was advanced on every tick, not only when an entry
  was actually written, so the repeat clock reset each minute and could never
  reach any interval longer than a tick. An 8-hour silenced firing now leaves
  one entry per repeat interval, as documented, instead of a single line.
- **One stray line of input killed the whole MCP server.** The stdio loop
  skipped lines that were not JSON, but valid JSON that is not an object — a
  JSON-RPC batch array, a bare `null` — threw out of the read loop and exited
  the process mid-session. Such lines are now skipped like unparseable ones,
  and a request id that is a non-integral number no longer throws while
  serialising the response.
- **A failed pull left the .env pinned to an image that never ran.** The deploy
  pins `PINQOPS_IMAGE` before pulling and restored only `PINQOPS_TAG` when the
  pull failed, so after a repository rename plus a registry hiccup the .env
  described a new-image/old-tag combination that never existed; the next
  **Apply** or restart then tried to recreate a healthy container from it. The
  pull-failure path now restores the image exactly as it restores the tag.
- **An indented `name:` anywhere in a compose file was read as the project
  name.** The ownership check parses the top-level `name:` key to learn which
  repository a compose project belongs to, but it matched trimmed lines, so
  e.g. `networks: default: name: shared-web` claimed the project for
  "shared-web" and every CI deploy failed with a misleading "one application
  per compose file" error. Only an unindented key counts now.
- **The unserved-port warning judged every service by the first image's
  `EXPOSE` list.** Add a database service with its own published port and every
  successful deploy logged a false "the image only exposes …" warning against
  the app image (and a genuine mismatch could hide behind the wrong image's
  ports). Each `compose ps` entry is now checked against its own image.
- **Reinstalling the proxy could silently drop a domain route added at the same
  time.** The install wrote the domain config through a raw load/save pair
  instead of the store's locked update, reopening exactly the lost-update
  window the lock exists to close — a route added or toggled concurrently
  vanished from the config and from the Caddyfile written right after.
- **Preview PR links were dead for apps connected via SSH remotes.** The
  previews list appended `/pull/<n>` to the stored remote verbatim, so
  `git@github.com:owner/repo.git` produced a link that is not a URL (and
  `.git`-suffixed HTTPS remotes fared little better). The link is now built
  from the parsed repository.
- **Disposing the GitHub dashboard service disposed a caller-supplied
  `HttpClient`.** The service tracks whether it owns its client precisely so a
  shared one survives disposal, but `Dispose` ignored the flag — every later
  use of the shared client threw `ObjectDisposedException`. It now disposes
  only a client it created itself.
- **An empty POST to the login form was an HTTP 500 and a stack trace in the
  log.** `Safe()` turns a malformed, mistyped or empty JSON body into a 400 for
  every route it wraps, but the three auth-handshake routes — `login`, `setup`
  and `change-password` — run outside it, so a body that could not be read
  escaped as an unhandled `JsonException`. Two of them are reachable without a
  session, so any caller could fill the journal with stack traces, and because
  the exception unwound past the audit middleware the attempt left no audit line
  either. All three answer 400 now, like their forty siblings.
- **Redis, KeyDB, NATS and SurrealDB reported "no stored credentials" for the
  password you cannot connect without.** Every catalog app is installed with a
  generated password, and the credentials dialog hides the store's raw
  `password` entry because for most apps it merely repeats a named one
  (`POSTGRES_PASSWORD=…`). For the four apps whose password only ever reaches the
  container through its command line — `redis-server --requirepass`,
  `keydb-server --requirepass`, `nats --auth`, `surreal start --pass` — no named
  entry is ever recorded, so hiding it unconditionally left the dialog empty for
  exactly the apps whose catalog note promises a generated password, with nothing
  anywhere that could recover it. It is now hidden only when another entry
  already carries the same value.
- **A rollback on one app blocked every other app on the server, and could make a
  successful one report failure.** One server hosts as many apps as you like, but
  the deploy gate and the rollback job were single process-wide fields rather than
  per compose project. So rolling back app A refused app B's rollback with "a
  rollback is already in progress", refused B's **Apply** on the `.env` editor
  with a message naming *this* project, and — because only one job was ever
  tracked — answered A's own progress poll with "unknown rollback job" the moment
  B started one, turning a rollback that had in fact succeeded into an error
  toast. Both are now keyed by the compose file.
- **An IPv6-only server could not be registered under Settings → Servers.** The
  SSH host validator accepted only hostnames and IPv4 literals, so every IPv6
  address was refused with "a valid SSH host (name or IP address) is required" —
  naming the very thing that had just been typed. Plain hex-and-colon IPv6
  literals are accepted now; a `%`-scoped form still is not, because it would be
  read as an unknown token in `ssh_config` and break the whole generated block.
- **Re-saving an unchanged host port could be rejected as "already in use".** The
  publish wizard's live port check and the `.env` editor both compared the stored
  `PINQOPS_HOST_PORT` to the entered port as raw text, and the env reader returns
  the value exactly as written. A hand-edited `PINQOPS_HOST_PORT= 8080` therefore
  never matched, and the app's own currently bound port was reported as taken.
  Both compare the parsed number now, like every other reader of these values.
- **The Backups page offered a viewer a form and buttons that could only ever be
  refused.** Scheduling, enabling, deleting a target and deleting a snapshot are
  all admin-only server-side, and running one on demand needs the deploy scope,
  but the page rendered every control for every role — while the download and
  restore buttons beside them were already gated. They now follow the same rule.
- **Handing a container to someone did nothing when it was addressed by id.**
  Container ownership is keyed by container *name* everywhere it is written —
  creating a container, installing a catalog app, renaming one — and both of
  those routes resolve an id to its name before touching the record. The
  assignment route did not: it stored whatever `{id}` happened to be. Every
  listing returns ids, so an admin handing over a container from a script wrote a
  record under the id, the gate found nothing for the name-addressed requests the
  dashboard sends, and the container stayed admin-only however many times it was
  handed over. It now resolves the name first, like its siblings.
- **A corrupt or unreadable ownership file could 403 or 500 every container
  action at once.** The store promises that a broken `container-owners.json`
  falls back to admin-only management rather than locking anyone out, but the
  legacy-key migration folded a pre-environments `web` and an already-migrated
  `local/web` onto one key and threw for the collision, and an I/O or permission
  error was not caught at all. Both run inside the ownership middleware, outside
  the handler error boundary, so either one was an unhandled failure on every
  governed request. The map is now built entry by entry — the namespaced record
  wins, because it is the one the running version wrote — and a file that cannot
  be read reads as "nothing is owned".
- **Deleting a domain reported success without deleting anything.** The delete
  route matched on the raw path segment lower-cased, while adding a domain stores
  the form `DomainName.Normalize` produces — which also strips the trailing dot of
  an absolute FQDN. A domain addressed as `app.example.com.`, or simply mistyped,
  matched nothing, and the route still answered `ok` and reloaded Caddy, so the
  route it was meant to take down carried on serving while the UI reported it
  gone. Both delete and toggle now fold the caller's spelling exactly the way the
  entry was stored, and an unknown domain is a 404 rather than a silent success.
- **A daily or weekly backup whose hour was missed was skipped for a whole day —
  or a whole week.** The schedule only ever fired inside its one-hour window, so
  a host that is off overnight, a dashboard restarted across 03:00, or a tick that
  overran the window meant the backup simply never ran, and the only symptom is a
  snapshot that is not there when it is needed. An established target now also
  runs once it is clearly overdue (25 hours for daily, 8 days for weekly), at
  whatever hour the next tick reaches it. A target that has never run still waits
  for its window — nothing is overdue about a target added a minute ago, and
  saving the form should not start a dump of a live database. A clock stepped
  backwards cannot make an already-run target look due again.
- **A failed redis restore left the container stopped.** The restore stops redis,
  replaces `dump.rdb` and starts it again, but nothing undid the stop if the copy
  failed — a snapshot deleted underneath it, a full disk, a daemon that went away
  — so a restore that reported an error had also taken the cache or session store
  down, and said nothing about it. The start is now in a `finally`; the original
  failure still surfaces.
- **Image retention did nothing, silently, for a compose file that pins by
  digest.** A digest's own colon (`repo@sha256:…`) sits after the last slash, so
  the rule that strips a `:tag` claimed it and produced the repository
  `repo@sha256` — a name nothing answers to. `docker images` listed no tags for
  it, so retention removed nothing and logged nothing, and the image directory
  grew for ever. A digest is now stripped before the tag is looked for.
- **The rotating JSONL writer counted one line too many.** With the line cap
  disabled (the alert trail rotates by size) the count ran *after* the append, so
  it counted the line just written and then added one on top of it — harmless
  while the cap is off, and a silent off-by-one the moment it is not. It also
  re-read the whole trail to get the wrong answer. Counted before the write now.
- **A second consecutive `pinqops rollback` rolled *forward* onto the release it
  had just escaped.** The search for a rollback target ignored `rolled_back`
  records, so the newest thing it could see was the tag the previous rollback
  had moved away from. It now follows the rollback chain, so repeated rollbacks
  keep moving backwards through history.
- **A deploy that hit its timeout recorded nothing and notified nobody.** The
  cancellation propagated straight out, past the one place that writes the
  history record and sends the notification — so the outcome that most needs
  reporting (docker killed mid-flight, project possibly half-updated) was the
  only one that produced neither, and the operator saw "A task was canceled."
  Both a timeout and an external cancellation are now recorded and reported. A
  health-check timeout at or above the deploy timeout is also rejected up front:
  it can only ever end in that timeout.
- **Restoring a corrupt volume snapshot emptied the volume and then failed.** The
  destructive `find … -delete` ran unconditionally, before anything established
  the archive was even readable. The archive is now listed first, chained so the
  delete is unreachable if that fails. A failed backup also no longer leaves a
  truncated archive that the snapshot list presents as an ordinary backup.
- **Image retention could delete every image production could roll back to.** PR
  previews pull the same repository with their own `sha-` tags on the same
  daemon, so a handful of preview builds filled the retention window. Tags named
  in deploy history and tags in use by a running container are now protected. On
  a host whose local time is not UTC, the `CreatedAt` parser also failed on every
  entry, silently disabling the explicit newest-first sort it feeds.
- **Preview teardown deleted the project's compose file even when
  `compose down` failed**, orphaning the containers, volumes and host port with
  nothing left that named the project — and reported success, so the workflow's
  teardown job went green. It now keeps the directory and fails loudly.
- **Preview domains were never actually served.** The runner saved `domains.json`
  but never regenerated the `Caddyfile`, which is the only file Caddy reads, so
  `pr-<n>.<domain>` was recorded and advertised but routed nowhere. Redeploying a
  preview also moved its host port on alternating pushes, because the probe for a
  free port saw the preview's own running container as a conflict.
- **Remote environments could never connect.** Pinned host keys are written under
  the pinqops alias, but ssh looks them up under the real hostname unless
  `HostKeyAlias` says otherwise — so with strict host-key checking every remote
  environment was refused. The **Test** button also reported "connected" for
  every such failure, because `docker version` prints client-only output and
  exits non-zero when the daemon is unreachable.
- **Alerts.** A "for" window could be satisfied by a gap in the samples rather
  than by a sustained breach — with the defaults the no-data grace is exactly as
  wide as the window, so one breach, five silent minutes and a second breach was
  enough to fire. A series that went no-data and came back still breaching sent a
  second `firing` with no `resolved` between them and lost the episode's real
  duration. A silenced rule wrote an identical trail entry every minute, rotating
  the real history out. And the 20-per-batch delivery cap truncated in
  rule-creation order, so one noisy wildcard rule could starve a critical alert of
  its only notification — there is no retry once a transition has been marked
  announced.
- **Concurrent edits silently dropped each other's changes.** The compose `.env`,
  the backup targets, the proxy domains and the alert state were each read,
  modified and written without holding anything across the sequence. The worst
  case: a dashboard `.env` edit erasing the tag a rollback had just pinned, so
  `compose up -d` deployed the default image while history recorded the rollback
  as successful. The `.env` lock is cross-process, because the CLI on the runner
  writes the same file.
- **`/api/compose/apply` ran `docker compose up -d` without taking the deploy
  lock**, so it could recreate the containers from under a running rollback. It
  now waits its turn and reports that one is in progress.
- **One malformed backup target made the whole Backups page unreachable.** The
  target's name was never validated when it was stored, and the list endpoint
  then failed on it every time — including the request that would have let anyone
  delete it. Names and engines are validated on write, and the list degrades per
  row. A second database target also silently overwrote the first: its id came
  from the engine alone, so every postgres container mapped to `db-postgres` and
  shared one snapshot directory and retention count.
- **Backups quietly ignored the selected server.** Targets carry no environment
  and the service always used the local daemon, so `?env=prod` dumped the *local*
  container of that name. Non-local environments are refused until targets can
  name one.
- **An install taking longer than 15 minutes reported as a failure.** Job
  retention was measured from when the install started rather than when it
  finished, so the poll that should have reported success was the one that
  discarded the record — on a slow uplink the normal case, since an image pull is
  given 30 minutes. The runner tarball download had the same shape of bug: it
  used `HttpClient`'s 100-second default timeout for a ~180 MB file, so any
  connection slower than ~15 Mbit/s failed at the same point every time.
- **`/api/audit/verify` reported tampering after a restart.** The hash chain's
  predecessor was read after rotating the log rather than before, so the first
  entry written by a process whose first append triggered a rotation chained from
  nothing while its real predecessor sat in the rotated file.
- **Bulk container actions could fire at the wrong server.** Switching servers
  left the selection in place, and the bulk handler resolves targets by name
  against the newly selected host — where names like `db`, `web` and every
  `pinqops-<app>` repeat. The container view's state is now cleared on a switch.
- **"Open" links pointed at the dashboard's own host** even when the listing they
  came from belonged to a remote server, so they opened the wrong machine (or
  nothing).
- **Nine error toasts fired on session expiry.** Each suppressed itself by
  comparing the error message against a string nothing ever threw, so the guard
  was dead and every one of them appeared on top of the lock screen.
- **Editing an alert rule silently reset it.** A repeat interval that was not one
  of the presets became "notify once", and a rule scoped to one container was
  widened to all of them whenever that container was missing from the latest
  sample — which is every container while docker is unreachable. Assigning an
  unmatched value to a `<select>` leaves it empty, and the form sent that.
- **Malformed request bodies returned 500 with a correlation id** on about forty
  endpoints, while four siblings returned 400 for the same input. A JSON `null`
  in the compose `.env` editor likewise crashed for a port key and returned 400
  for every other key.
- **A mistyped CLI flag changed what was deployed and exited 0.** A flag in last
  position read as absent, so `pinqops rollback --to` silently rolled back to the
  history default instead of erroring.
- Smaller fixes: the container-name link is ownership-gated like the buttons
  beside it; `catalog:REDIS` no longer derives an unroutable container name; a
  domain can no longer be saved with an out-of-range port that Caddy then skips;
  preview routes are no longer reported as drifted; the reverse proxy no longer
  errors when it is installed but stopped; container ownership follows a rename
  or removal addressed by id rather than by name; a backup target that has never
  run reads as "never" instead of "739983d ago"; the publish wizard's progress bar
  no longer overshoots 100%; and the GitHub overview cache no longer expires
  before every auto-refresh, which was spending 3-9 REST calls per idle tick.
- `dotnet build -c Release` is warning-free again.

- **The Overview page threw a `TypeError` instead of drawing the host load and
  memory sparklines.** Two different functions were both declared `sparkline` in
  the dashboard's single script block, so the second one won and the Overview
  call reached it with a DOM element where it expected an array of samples —
  `Spread syntax requires ...iterable[Symbol.iterator] to be a function`. The
  container-detail one is now `dtSparkline`, and the Overview sparklines fill
  their card's width (a fixed pixel height letterboxed the plot into a strip
  down the middle) and draw a steady series through the middle rather than
  pinned to the floor, where it read as "no data".
- **The "Add an SSH host" form stacked its six fields one per row.** It asked
  for a `grid2` class that no stylesheet ever defined; it now uses the shared
  two-column `form-grid`. (It lives in Settings → Servers now.)
- **The server list and the compose `.env` editor were fighting over the same
  names.** Both tables were `#env-table` and both used `ev.*` translation keys,
  so `ev.title`, `ev.none` and `ev.removed` were each defined twice in one
  object literal — the second definition won, and the `.env` editor's heading
  read "Environments". The server list also rendered into the `.env` table, and
  the SSH private-key box shared `#env-key` with the `.env` KEY input, so adding
  a host sent the wrong field as the key. Servers now own `eh.*` and their own
  element ids.
- **A deploy refuses to run against another application's compose project.** The
  wizard's guard only covered *creating* a compose file; once one existed, a
  second repository's deploy pinned its own image and tag straight over the
  first application's, so the wrong image ran under the wrong project name (or
  the pull died on a tag that only exists in the other package). `pinqops deploy
  --image` now compares the compose file's project name — which is the owning
  repository's — against the image being deployed, and fails **before writing
  anything**, naming both applications and the `APP_COMPOSE_PATH` variable to
  set. The previous image/tag check could not catch this: it ran *after* pinning,
  so it always compared the value with itself.
- **Connecting a repository no longer leaves the UI pointing at the previous
  one.** The wizard's connect step refreshed the GitHub cache but not the
  settings cache, so after switching repositories the readiness card kept the old
  name, the repo list kept its ✓ on the old entry, and **"Re-run the wizard" ran
  against the old repository**.
- **Two repositories can no longer silently share one compose project.** Nothing
  tied a compose file to a repository, so pointing a second repository at the
  same path let its deploy pin *its* tag onto the *first* one's image — and die
  pulling a tag that only exists in the other package (an opaque `403`). The
  wizard now reads the project name out of an existing compose file and refuses
  with the owning repository named, plus the path and `APP_COMPOSE_PATH` variable
  to set for a second app.
- **A deploy that publishes a port nothing listens on is no longer reported
  green.** The container runs, so the health check passed and the deploy was
  recorded successful while the site was unreachable. After a successful deploy,
  pinqops compares the published container port against the image's exposed ports
  and warns — naming `PINQOPS_CONTAINER_PORT` — when they disagree. Advisory
  only: `EXPOSE` is documentation, so it never fails a deploy.
- **The built image is now explicitly connected to its repository.** The
  generated workflow labels the image with
  `org.opencontainers.image.source`, which is what grants the deploy job's
  `GITHUB_TOKEN` read access to the package. Relying on the implicit link was the
  cause of a deploy that could *push* an image and then get `403 Forbidden`
  *pulling the same tag seconds later* — most easily reached by renaming a
  repository, since packages are not renamed with it and the new name is a new
  package with its own access. Docs no longer claim the link "happens
  automatically" and now document the `403` recovery.
- **The image name is lowercased.** `github.repository` preserves the
  repository's real case while registries require a lowercase name, so any owner
  or repo with a capital letter failed the build outright with `repository name
  must be lowercase`. The workflow resolves the name once per job, and
  `pinqops deploy --image` rejects an uppercase repository rather than pinning a
  reference docker cannot resolve.
- **A deploy can no longer be cancelled half-done.** `cancel-in-progress` was set
  workflow-wide, so a second merge killed an in-flight deploy — potentially
  between compose stopping the old container and starting the new one, leaving
  the app down with no history record and no notification, and a `.env` pinned to
  a tag that was never live. Builds stay cancellable (a superseded build is
  waste); deploys now queue.

### Added

- **Port collisions are caught before they can take the app down.** A published
  port that is already bound only announced itself as a failed `docker compose
  up -d` — and because compose removes the old container before creating the new
  one, a collision left the app *stopped*, not merely un-updated. Now: the wizard
  picks the first free host port from `8080` when generating the project, and the
  `.env` editor rejects a `PINQOPS_HOST_PORT` that is out of range or already in
  use, naming the consequence. Deploy-time probing is deliberately *not* done —
  the app's own running container holds the port, so every normal redeploy would
  look like a conflict.
- **Deploy failures report docker's actual reason.** The deploy record and the
  webhook/Slack/Telegram notification carried a bare `compose up failed` while
  the real cause (`port is already allocated`, `denied: permission_denied`, …)
  was only in the log. The most specific line of docker's stderr is now included,
  capped so it stays readable in a chat message.
- **The generated compose project now publishes a port, so a deployed app is
  actually reachable.** Previously the template left `ports:` commented out: the
  container came up but `docker ps` showed only `80/tcp` with nothing mapped. The
  wizard now reads the container port from the repository's own Dockerfile
  (`EXPOSE`, falling back to `80`) and writes
  `ports: ["${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT}"]`, seeding both
  values into the project `.env`. Changing the published port is a `.env` edit in
  **Deployments → Environment** plus **Apply** — no YAML editing.
- **Containers are named after the repository.** The compose project name was the
  fixed string `pinqops`, so every deployment's container was `pinqops-app-1`
  regardless of what was deployed — and indistinguishable from the catalog apps.
  It is now the repository name (reduced to compose's grammar the same way
  compose would), so the container reads `<repo>-app-1`, e.g. `peramice-app-1`.
- **The deployed image now follows the repository automatically (no more stale
  compose after a rename).** The generated compose references
  `image: ${PINQOPS_IMAGE:-…}:${PINQOPS_TAG:-latest}`, and `pinqops deploy
  --image ghcr.io/${{ github.repository }}` (passed by the generated workflow)
  pins `PINQOPS_IMAGE` in the project `.env` before pulling — just like the tag.
  Rename the repository and the next deploy pulls the new image with zero manual
  intervention. Before pulling, pinqops verifies the compose resolves to the
  expected image; an image line hand-edited to hardcode a name (the classic cause
  of an opaque `403`/`denied` on pull) fails fast with the exact fix.
- **Dashboard: runner service logs and multi-runner visibility (Runner view).**
  The Runner view now lists every `actions.runner.*` service on the host (a
  server can carry more than one after re-registering to a new repository) with
  its live state, and a **logs** button shows each service's last 100 journal
  lines — enough to diagnose a runner that is registered but not picking up jobs
  without opening an SSH session.

### Fixed

- **Atomic, owner-only writes for secret state.** `ui.json` (GitHub PAT),
  `app-credentials.json` (plaintext app passwords), the compose `.env`, and
  deploy history are now written via a temp-file-plus-rename that
  creates the file `0600` *before* any bytes are written. This closes a
  create-then-`chmod` window where secrets briefly existed at the process umask,
  and prevents a crash mid-write from truncating `ui.json` and silently dropping
  the dashboard back to the unauthenticated setup flow.
- **Docker argument injection hardening.** Container/network names that begin
  with `-` are now rejected, and every dashboard docker
  call passes `--` before the user-supplied positional, so a crafted name can no
  longer be parsed as a docker flag.
- **Image retention no longer trusts `docker images` ordering.** Retention now
  sorts `sha-*` tags by `CreatedAt` before keeping the newest N, so an
  out-of-order-built or re-pulled image can't cause the newest image (the one a
  rollback needs) to be deleted.
- **Dashboard robustness.** `GitHubDashboardService` no longer disposes an
  injected `HttpClient`, and its JSON reader tolerates `null` nodes (e.g. a
  workflow run with `actor: null`) instead of throwing and failing the whole
  overview. Malformed `docker --format json` lines are skipped rather than
  discarding every result.
- **Auth & input hardening.** The first-run setup code is widened to 64 bits and
  the setup endpoint is now covered by the brute-force throttle; generated
  passwords use rejection-sampled selection (no modulo bias); the OAuth
  device-flow handle table is swept and capped; `install-service` validates its
  arguments before writing the systemd unit; and repository owner/name parsing
  enforces GitHub's character set.

## [0.5.0] - 2026-07-19

### Added

- **Safe deploys: SHA tags, history, health checks and rollback.** Builds now
  push an immutable `sha-<commit>` tag alongside `:latest`, and
  `pinqops deploy --tag sha-<commit>` pins it in the compose project's `.env`
  (compose file references the image as `:${PINQOPS_TAG:-latest}` — fully
  backward compatible without a `.env`). After `up -d` the services are
  health-checked (`compose ps` until running/healthy, default 60s,
  `--health-timeout-seconds`, 0 skips); every deploy is recorded in
  `.pinqops/history.json` next to the compose file. New commands:
  **`pinqops rollback [--to <tag>]`** (defaults to the last successful tag;
  uses the locally kept image, so no registry login needed) and
  **`pinqops history [--json]`**. Instead of blanket image pruning, the newest
  N `sha-*` images are kept for rollback (`--keep-images`, default 5). There is
  deliberately **no automatic rollback** — a failed deploy shows red in CI and
  rolling back is an explicit operator action. The dashboard's Deployments view
  gains a deploy-history card with the current version and a one-click
  **Roll back** button.
- **Notifications.** Deploy results (success, failure, health-check failure,
  rollback) are sent to a generic **webhook** (full JSON), **Slack**-compatible
  incoming webhooks (also Discord `/slack`, Mattermost) and **Telegram** bots.
  Configured per event and per channel in `.pinqops/notify.json` (0600) next to
  the compose file, so CLI deploys on the runner and dashboard rollbacks both
  notify. Settings UI with per-channel Test buttons. Best-effort by design: a
  notification failure never fails a deploy.
- **Generated catalog passwords + credential storage.** Catalog apps no longer
  ship hardcoded defaults (`postgres/pinqops` etc.) — every credential is
  generated per install (CSPRNG, 20 chars) and stored 0600 in
  `~/.config/pinqops/app-credentials.json`. The dashboard shows them after
  install and behind a key button on installed apps (masked, reveal/copy). A
  reinstall reuses the stored password so data in surviving volumes keeps
  working; WordPress automatically receives the MySQL app's password. A guard
  test keeps hardcoded passwords from coming back.
- **Compose `.env` editor.** The Deployments view manages the compose project's
  `.env` (masked values, `PINQOPS_TAG` shown read-only) with an explicit
  *Apply* that recreates containers via `compose up -d`.
- **Domains & SSL (Caddy reverse proxy).** A new dashboard view installs a
  managed `pinqops-caddy` container publishing 80/443 with automatic Let's
  Encrypt certificates (persisted in named volumes). Routes map a domain to a
  container port over the shared `pinqops-apps` network; the Caddyfile is
  generated from strictly validated fields and hot-reloaded. The generated
  compose template now joins `pinqops-apps` so the deployed app is routable by
  container DNS.
- **First web test project** — `tests/PinqOps.Web.Tests` covers catalog
  password substitution, the credential store, docker run arguments, the
  Caddyfile generator (golden + injection rejection) and the Caddy service
  sequences.

### Changed

- `examples/workflows/deploy.yml`, the dashboard's generated workflow/compose
  templates and `deploy/app.docker-compose.example.yml` moved to the SHA-tag +
  `${PINQOPS_TAG:-latest}` scheme. Existing users: add the interpolation to
  your compose file's `image:` line to enable history/rollback — nothing breaks
  if you don't.
- `docker image prune -f` after deploys is replaced by tag-aware retention
  (keep `latest` + newest N `sha-*`), then a dangling-layer prune.

## [0.4.0] - 2026-07-18

### Added

- **`pinqops-ui`** (`src/PinqOps.Web`) — an optional, self-contained web
  dashboard for the server (default port `7467`). Password-protected; connect
  it to GitHub with the repo URL plus a PAT (or username + token). Shows
  containers (start/stop/restart/logs/inspect/stats), images, volumes,
  networks, Docker disk usage, the compose project, workflow runs, GitHub
  runner status and the last job the self-hosted runner executed, the local
  runner's systemd service, host health (disk/memory/load/uptime), and a
  one-click deploy. Ships as a single binary attached to releases; the CLI
  works fully without it.
- **`pinqops-ui install-service`** — installs the dashboard as a systemd
  service (enabled + started), so it keeps running after the SSH session ends
  and comes back after a reboot. `uninstall-service` removes it; the first-run
  setup code is in `journalctl -u pinqops-ui`. Also adds `version` / `help`
  subcommands.
- **Sign in with GitHub** — the dashboard can authenticate via the OAuth
  device flow (bring your own OAuth App client id; no secret, no callback
  port), or with a pasted token as before. Either way it now shows who is
  signed in and lets you **pick the repository from the list of repos your
  account is authorized for** instead of typing a URL.
- **Turkish localization** — the entire dashboard is available in Türkçe
  (EN/TR switch in the top bar; auto-detected from the browser, remembered).
- **Docker network management** — create networks (driver + internal flag),
  inspect them (subnet, gateway, attached containers), connect/disconnect
  containers, and remove non-built-in networks, all from the Storage &
  Networks panel.
- **Professional visual refresh** — grouped sidebar with vector icons, a
  refined dark palette, consistent buttons/inputs/chips, focus states, and
  polished tables/cards across every view.
- **Portainer-style onboarding.** No repository URL typing: picking a repo
  from the authorized list connects it immediately, and a new *Deployment
  readiness* panel checks the whole pipeline — Dockerfile present, deploy
  workflow present, runner installed/online, compose project present — and
  fixes what it can: one click commits `.github/workflows/deploy.yml` to the
  repo, generates the server compose file for the repo's GHCR image, or
  **installs and registers the self-hosted runner from the dashboard**
  (registration token via the stored PAT, same code path as
  `pinqops install-runner`). A missing Dockerfile is called out as the only
  thing expected from the repo.
- **App catalog.** ~50 curated one-click installs (Redis, PostgreSQL, MySQL,
  MongoDB, RabbitMQ, Kafka, Elasticsearch, MinIO, Grafana, Prometheus,
  Uptime Kuma, Gitea, Jenkins, Keycloak, Vaultwarden, Nextcloud, n8n, …)
  grouped by category with search, editable host port, open-in-browser links,
  and safe removal (volumes kept). The API only accepts fixed catalog specs —
  it can never be used to run an arbitrary image.
- **Network map.** The Storage & Networks panel renders a live SVG diagram of
  which containers sit on which Docker networks.

### Changed

- **README** compressed further; a web UI is no longer out of scope.
- **release.yml** now also publishes the `pinqops-ui` binary.

### Fixed

- **Setup screen flipped to the login form mid-paste.** The dashboard's
  auto-refresh timer ran before sign-in; its 401 responses switched the
  first-run setup form into the password login form and cleared the inputs
  (typically while pasting the setup code). Refresh now only runs once signed
  in, and a 401 only returns to the login screen when a real session expires.
- **`pinqops version` always reported `1.0.0`.** The release workflow never
  stamped the git tag into the published binaries, so every release carried the
  SDK's default assembly version — updating the binary looked like a no-op even
  though the code changed. `release.yml` now passes `-p:Version=<tag>` to both
  publishes, the CLI prints the stamped (informational) version, and the web UI
  shows it in the sidebar footer and its startup line.

### Security

- **`pinqops-ui` hardening.** First-run password creation requires a one-time
  setup code printed on the server console; login and password change are
  brute-force throttled (per-client lockout) on top of a per-client API rate
  limit; PBKDF2 iterations raised to 600k (legacy hashes upgrade on login);
  all sessions are revoked on password change; a strict Content-Security-Policy
  pins the dashboard's inline script by SHA-256 hash; hardened response headers
  (`X-Frame-Options`, `nosniff`, `Referrer-Policy`, COOP/CORP, HSTS on TLS);
  request bodies capped at 64 KB; optional HTTPS via `--cert <pfx>`; auth
  events are logged; the unauthenticated state endpoint no longer reveals
  whether GitHub is configured. See the new web-UI section in SECURITY.md.

## [0.2.1] - 2026-07-15

### Fixed

- **Runner registration failed with "cannot start process './config.sh'".** The
  installer invoked the runner's `config.sh` by a relative path, but .NET
  resolves a relative executable against the current process's directory, not the
  child working directory — so it was not found unless pinqops ran from
  `/opt/actions-runner`. It is now invoked by its full path. Affected both
  `pinqops setup` and `pinqops install-runner`.
- **Registering as root now works.** The installer sets `RUNNER_ALLOW_RUNASROOT=1`
  (ignored for non-root users), so `config.sh` no longer refuses on a root-only
  server.

## [0.2.0] - 2026-07-15

### Added

- **`pinqops setup`** — a guided, one-command onboarding wizard for a fresh
  server: it checks prerequisites (docker, docker compose, tar, systemd) and
  prints exact install commands when any are missing, obtains a runner
  registration token (authenticated `gh` CLI → a PAT via the GitHub API → a
  pasted token), installs and registers the self-hosted runner, and prints the
  remaining compose steps. Scriptable via flags/env and `--non-interactive`.
- **[docs/TOKENS.md](docs/TOKENS.md)** — centralizes registration-token vs PAT
  guidance and explains why deploys need no git token on the server.

### Changed

- **README** slimmed to a one-screen quickstart centered on `pinqops setup`,
  with deeper material linked under `docs/`.
- **docs/SETUP.md** now features `pinqops setup` as the primary path, keeping the
  manual step-by-step as the equivalent/advanced route.

## [0.1.0] - 2026-07-15

### Added

- Initial release of **pinqops**, a minimal DevOps CLI + pipeline for
  auto-deploying Docker apps to a fully closed server (no inbound ports).
- **`pinqops` .NET 10 CLI** (`src/PinqOps.Cli` + `src/PinqOps.Core`):
  - `pinqops deploy` — runs the fixed `docker compose pull && up -d` against a
    fixed compose project (arguments built as discrete list items; no injection).
  - `pinqops install-runner` — downloads, registers, and installs a GitHub
    Actions self-hosted runner as a systemd service (outbound-only; label
    `pinqops-prod`).
- **xUnit tests** (`tests/PinqOps.Core.Tests`) covering command building,
  option validation, the deploy sequence (via a fake process runner), and the
  runner-install orchestration.
- **Workflows:** `ci.yml` (dotnet build + test on PRs) and `release.yml`
  (tag → publish a self-contained linux-x64 `pinqops` binary). A deploy pipeline
  **template** ships under `examples/workflows/deploy.yml` for consumers to copy
  into their own app repo (push to `master` → cloud build + GHCR push → deploy
  job on the self-hosted runner).
- Example files: the fixed application compose project and an example
  application Dockerfile.
- Documentation: README, ARCHITECTURE, SETUP, CONFIGURATION, SECURITY,
  CONTRIBUTING, CODE_OF_CONDUCT, and issue/PR templates.

### Security

- The production server exposes no inbound ports; the runner only dials GitHub
  and GHCR outbound.
- No long-lived secret is stored on the server: registry auth uses the per-job
  `GITHUB_TOKEN`.
- `pinqops deploy` never checks out or executes repository content on the
  server, and the workflow triggers only on `push: master`.
