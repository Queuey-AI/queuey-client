# Queuey Edge as a container: the self-contained queuey binary from a GitHub
# release, on Debian slim. Debian, not Alpine: the release binaries are built
# against glibc (linux-x64, linux-arm64), and Alpine has musl.
FROM debian:bookworm-slim

ARG QUEUEY_VERSION=0.1.0-preview.10
ARG TARGETARCH

RUN set -eu; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    case "$TARGETARCH" in \
      arm64) rid=linux-arm64 ;; \
      amd64) rid=linux-x64 ;; \
      *) echo "No queuey binary for $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    curl -fsSL "https://github.com/Queuey-AI/queuey-client/releases/download/v${QUEUEY_VERSION}/queuey-${QUEUEY_VERSION}-${rid}.tar.gz" \
      | tar -xz -C /usr/local/bin queuey; \
    chmod 0755 /usr/local/bin/queuey; \
    apt-get purge -y curl; \
    apt-get autoremove -y; \
    rm -rf /var/lib/apt/lists/*; \
    useradd --system --uid 10001 --home-dir /var/lib/queuey --shell /usr/sbin/nologin queuey; \
    install -d -o queuey -g queuey -m 0700 /var/lib/queuey /var/cache/queuey

# The binary unpacks SQLite's native library when it starts, so it needs a
# writable directory it may also execute from. A volume, not a tmpfs: Docker
# mounts tmpfs noexec.
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/cache/queuey

USER queuey
VOLUME ["/var/lib/queuey", "/var/cache/queuey"]

# The spool must live on the volume: accepted events wait there through
# outages and restarts.
ENTRYPOINT ["queuey", "edge", "run", "--spool", "/var/lib/queuey/spool.db"]
