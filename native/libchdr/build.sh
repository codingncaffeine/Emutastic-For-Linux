#!/usr/bin/env bash
# Builds libchdr.so from the vendored source (../libchdr-src, master @ a369a70).
# Deps (lzma/miniz/zstd) are bundled in the source tree; cmake builds them in.
set -euo pipefail
cd "$(dirname "$0")"
SRC=../libchdr-src

cmake -S "$SRC" -B build -DCMAKE_BUILD_TYPE=Release \
      -DBUILD_SHARED_LIBS=ON -DWITH_SYSTEM_ZLIB=OFF \
      -DCMAKE_POSITION_INDEPENDENT_CODE=ON > /dev/null
cmake --build build -j"$(nproc)" > /dev/null
cp build/libchdr.so* . 2>/dev/null || cp build/src/libchdr.so* . 2>/dev/null
# Normalize the SONAME variants to one loadable name.
[ -f libchdr.so ] || cp "$(ls libchdr.so.* | head -1)" libchdr.so
echo "[libchdr] built $(ls libchdr.so*)"
