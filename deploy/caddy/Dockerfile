# pinqops' Caddy: stock Caddy plus the modules pinqops needs compiled in.
#
# Why not caddy:2-alpine — Caddy ships no rate-limiting module at all, and every
# DNS provider is a separate module. Rate limits per domain and wildcard
# certificates (which need a DNS-01 challenge, because HTTP-01 cannot cover a
# wildcard) are therefore not configuration away; they have to be built in.
#
# Kept to a short, deliberate list. Every module here is one more thing that can
# break a proxy that every domain on the server depends on, so a provider is added
# when someone needs it rather than because it exists.
FROM caddy:2-builder-alpine AS builder

RUN xcaddy build \
    # Per-domain and per-route rate limiting.
    --with github.com/mholt/caddy-ratelimit \
    # DNS-01 challenge providers, for wildcard certificates.
    --with github.com/caddy-dns/cloudflare \
    --with github.com/caddy-dns/route53 \
    --with github.com/caddy-dns/digitalocean

FROM caddy:2-alpine

# Only the binary changes. The base image keeps the same entrypoint, the same
# /data and /config layout and the same non-root story, so a proxy reinstalled
# onto this image finds its existing certificates exactly where it left them.
COPY --from=builder /usr/bin/caddy /usr/bin/caddy

# Fails the build rather than shipping an image whose modules did not make it in.
RUN caddy list-modules | grep -q 'http.handlers.rate_limit' \
    && caddy list-modules | grep -q 'dns.providers.cloudflare'
