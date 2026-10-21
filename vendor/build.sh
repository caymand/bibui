#!/usr/bin/env bash
# Builds FreeType and HarfBuzz into bibui-core/native/<rid>, for the machine it runs on.
# FreeType has only the modules bibui uses (ftmodule.h);
# HarfBuzz is its one-file build, with no FreeType, glib or ICU.
set -euo pipefail
cd "$(dirname "$0")"
ft=freetype-2.15.0
hb=harfbuzz-14.6.0

case "$(uname -s)-$(uname -m)" in
  Darwin-*)      rid=osx-arm64;   lib=dylib; flags=(-dynamiclib -arch arm64 -arch x86_64 -mmacosx-version-min=12.0) ;;
  Linux-x86_64)  rid=linux-x64;   lib=so;    flags=(-shared -static-libstdc++ -static-libgcc) ;;
  Linux-aarch64) rid=linux-arm64; lib=so;    flags=(-shared -static-libstdc++ -static-libgcc) ;;
esac
out=../bibui-core/native/$rid
mkdir -p "$out"

cc -O2 -fPIC "${flags[@]}" -DFT2_BUILD_LIBRARY "-DFT_CONFIG_MODULES_H=<ftmodule.h>" \
  -I. -I$ft/include \
  $ft/src/base/{ftsystem,ftinit,ftdebug,ftbase,ftbitmap,ftglyph,ftbbox,ftmm}.c $ft/src/gzip/ftgzip.c \
  $ft/src/{truetype/truetype,sfnt/sfnt,smooth/smooth,autofit/autofit,psnames/psnames}.c \
  -o "$out/libfreetype.$lib"

c++ -O2 -fPIC "${flags[@]}" -fno-exceptions -fno-rtti $hb/src/harfbuzz.cc -o "$out/libharfbuzz.$lib"

# The macOS libraries are universal: the same files serve osx-x64.
if [[ $lib == dylib ]]; then cp "$out"/*.dylib ../bibui-core/native/osx-x64/; fi
