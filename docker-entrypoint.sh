#!/bin/sh
set -e

# Ownership of the blob-storage directory has to be fixed HERE rather than in the Dockerfile.
#
# The Dockerfile's own `chown -R dms:dms /app/storage` applies to the directory baked into the
# image. When a volume is mounted at that same path at runtime, it covers that directory
# entirely — and the volume's root arrives owned by root, which the non-root `dms` user the
# application runs as cannot write to. The symptom is an UnauthorizedAccessException from
# FileSystemBlobStore's constructor the first time anything touches storage, which in practice
# means /health/ready fails and the deployment never goes healthy.
#
# So: this script runs as root purely to correct the mount's ownership, then hands off to the
# application as `dms`. The application itself never runs as root.
STORAGE_ROOT="${STORAGE_ROOT:-/app/storage}"

if [ -d "$STORAGE_ROOT" ]; then
    chown -R dms:dms "$STORAGE_ROOT" 2>/dev/null || \
        echo "entrypoint: could not chown $STORAGE_ROOT (continuing; writes may fail)" >&2
fi

# exec so the application replaces this shell as PID 1 and receives SIGTERM directly —
# otherwise container shutdown waits for a process that never gets told to stop.
exec gosu dms "$@"
