#!/usr/bin/env bash
# Builds libtesseract.so (Tesseract + Leptonica in one shared library, C API exported) for linux-x64.
# Meant to run inside the container ubuntu:22.04 (glibc 2.35, GLIBCXX 3.4.30): container-build.sh starts it there.
#   packaging/native/build-linux.sh <repo-root> <out-dir>
# No OpenMP, no libcurl, no libarchive, no image decoders: nothing of that is installed in the container and every one is switched off.
set -euo pipefail

repo=$(cd "${1:?repo root}" && pwd)
out=$(mkdir -p "${2:?out dir}" && cd "$2" && pwd)
# shellcheck disable=SC1091
source <(grep -E '^[A-Z_]+=' "$repo/packaging/native/versions.txt")

export DEBIAN_FRONTEND=noninteractive
if ! command -v cmake >/dev/null || ! command -v git >/dev/null; then
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends build-essential cmake ninja-build git ca-certificates >/dev/null
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# PAGEWING_NATIVE_SOURCES=<dir with leptonica/ and tesseract/ checkouts> skips the clone (a machine whose container cannot reach GitHub);
# the commit is checked either way.
fetch() { # url tag commit dir
  name=$(basename "$4")
  if [ -n "${PAGEWING_NATIVE_SOURCES:-}" ] && [ -d "$PAGEWING_NATIVE_SOURCES/$name" ]; then
    cp -r "$PAGEWING_NATIVE_SOURCES/$name" "$4"
  else
    git clone --quiet --depth 1 --branch "$2" "$1" "$4"
  fi
  actual=$(git -C "$4" rev-parse 'HEAD^{commit}')
  [ "$actual" = "$3" ] || { echo "build-linux: $1 $2 is $actual, expected $3" >&2; exit 1; }
}

fetch "$LEPTONICA_URL" "$LEPTONICA_TAG" "$LEPTONICA_COMMIT" "$work/leptonica"
fetch "$TESSERACT_URL" "$TESSERACT_TAG" "$TESSERACT_COMMIT" "$work/tesseract"

cmake -S "$work/leptonica" -B "$work/lept-build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$work/prefix" -DBUILD_SHARED_LIBS=OFF -DBUILD_PROG=OFF -DSW_BUILD=OFF \
  -DENABLE_ZLIB=OFF -DENABLE_PNG=OFF -DENABLE_GIF=OFF -DENABLE_JPEG=OFF -DENABLE_TIFF=OFF -DENABLE_WEBP=OFF -DENABLE_OPENJPEG=OFF \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON
cmake --build "$work/lept-build" --target install

cmake -S "$work/tesseract" -B "$work/tess-build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=ON -DSW_BUILD=OFF -DOPENMP_BUILD=OFF -DDISABLE_CURL=ON -DDISABLE_ARCHIVE=ON \
  -DBUILD_TRAINING_TOOLS=OFF -DBUILD_TESTS=OFF -DGRAPHICS_DISABLED=ON -DENABLE_LTO=OFF \
  -DLeptonica_DIR="$work/prefix/lib/cmake/leptonica" \
  -DCMAKE_SHARED_LINKER_FLAGS="-static-libstdc++ -static-libgcc -Wl,--exclude-libs,ALL"
cmake --build "$work/tess-build" --target libtesseract

lib=$(find "$work/tess-build" -maxdepth 1 -name 'libtesseract.so*' -type f | head -1)
[ -n "$lib" ] || { echo "build-linux: libtesseract.so not found" >&2; exit 1; }
cp "$lib" "$out/libtesseract.so"
strip --strip-unneeded "$out/libtesseract.so"
echo "build-linux: $(ls -l "$out/libtesseract.so" | awk '{print $5}') bytes in $out/libtesseract.so"
