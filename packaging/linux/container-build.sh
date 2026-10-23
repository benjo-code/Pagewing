#!/usr/bin/env bash
# Builds, packages and smoke-tests the Linux release files inside ubuntu:22.04, so that the binaries link against
# glibc 2.35 (Docs/DECISIONS.md, entry "Mindest-glibc 2.35 und Bauweise der Linux-Pakete"). Called by release.yml:
#
#   docker run --rm -v "$PWD:/work" -v "$DOTNET_INSTALL_DIR:/dotnet:ro" -w /work \
#     -e VERSION -e DEBVERSION -e HOST_UID -e HOST_GID ubuntu:22.04 bash packaging/linux/container-build.sh
#
# The container has no GH_TOKEN and uploads nothing; the workflow uploads from the host. Files end up in out/release.
set -euo pipefail

: "${VERSION:?VERSION is not set}"
: "${DEBVERSION:?DEBVERSION is not set}"
: "${HOST_UID:?HOST_UID is not set}"
: "${HOST_GID:?HOST_GID is not set}"

# Files written as root would block the next checkout on the runner.
trap 'chown -R "$HOST_UID:$HOST_GID" /work' EXIT

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends \
  ca-certificates curl clang zlib1g-dev binutils dpkg squashfs-tools xvfb \
  libfontconfig1 libx11-6 libice6 libsm6 libicu70 libssl3 lintian desktop-file-utils

export PATH="/dotnet:$PATH" DOTNET_ROOT=/dotnet DOTNET_CLI_HOME=/tmp/dotnet-home DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
export HOME=/tmp/home XDG_CACHE_HOME=/tmp/home/.cache
# NuGet follows DOTNET_CLI_HOME, tools/licenses.cs follows HOME: one explicit folder for both.
export NUGET_PACKAGES=/tmp/nuget-packages
mkdir -p "$HOME"

ldd --version | sed -n 1p
dotnet --version

# The self-test of tools/package.cs runs on the host (release.yml): jammy's squashfs-tools 4.5 are older than the 4.6
# that its reproducibility checks assume.
dotnet run tools/licenses.cs -- selftest

dotnet publish src/Pagewing.Cli -c Release -r linux-x64 -p:Version="$VERSION" -o out/cli
dotnet publish src/Pagewing.App -c Release -r linux-x64 -p:Version="$VERSION" -o out/app

dotnet run tools/smoke-cli.cs -- out/cli/pagewing
dotnet run tools/licenses.cs -- check --publish-dir out/app --publish-dir out/cli

# Blocking: a binary that needs a newer glibc or libstdc++ than Ubuntu 22.04 offers must not be shipped.
files=$(find out/app out/cli -maxdepth 1 -type f \( -name Pagewing -o -name pagewing -o -name '*.so' -o -name '*.so.*' \) | sort)
echo "$files"
# shellcheck disable=SC2086
dotnet run tools/package.cs -- check-binary $files --max-glibc 2.35

dotnet run tools/package.cs -- layout --rid linux-x64 --app out/app --cli out/cli --out out/layout

dotnet run tools/package.cs -- deb --layout out/layout --version "$VERSION" --out "out/release/pagewing_${DEBVERSION}_amd64.deb"
dotnet run tools/package.cs -- deb-smoke "out/release/pagewing_${DEBVERSION}_amd64.deb"

dotnet run tools/package.cs -- runtime-fetch
dotnet run tools/package.cs -- appimage --layout out/layout --version "$VERSION" --out "out/release/Pagewing-$VERSION-x86_64.AppImage"
xvfb-run -a dotnet run tools/package.cs -- appimage-smoke "out/release/Pagewing-$VERSION-x86_64.AppImage"

# libfuse inside the AppImage runtime is LGPL-2.1: its source and the runtime's build scripts go to the release too.
dotnet run tools/package.cs -- runtime-sources --out out/release

dotnet run tools/package.cs -- sums \
  "out/release/pagewing_${DEBVERSION}_amd64.deb" \
  "out/release/Pagewing-$VERSION-x86_64.AppImage" \
  out/release/fuse-3.15.0.tar.xz \
  out/release/type2-runtime-20251108.tar.gz \
  --out out/release/SHA256SUMS-linux.txt
