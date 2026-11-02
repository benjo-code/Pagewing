#!/usr/bin/env bash
# Starts build-linux.sh in the container ubuntu:22.04 (the same floor as the release files: glibc 2.35).
#   packaging/native/container-build.sh [out-dir]       (default artifacts/native/linux-x64)
# PAGEWING_NATIVE_SOURCES=<dir with leptonica/ and tesseract/ checkouts> hands in sources cloned on the host instead of cloning in the
# container (a machine whose container cannot run git over https); build-linux.sh checks the commits either way.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
out=${1:-$repo/artifacts/native/linux-x64}
mkdir -p "$out"
extra=()
if [ -n "${PAGEWING_NATIVE_SOURCES:-}" ]; then
  extra=(-v "$PAGEWING_NATIVE_SOURCES:/sources:ro" -e PAGEWING_NATIVE_SOURCES=/sources)
fi
docker run --rm -v "$repo:/src:ro" -v "$(cd "$out" && pwd):/out" "${extra[@]}" -e HOME=/tmp ubuntu:22.04 \
  bash /src/packaging/native/build-linux.sh /src /out
