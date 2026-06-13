#!/bin/sh
# Cross-compile playfair.dll (x86-64) from the vendored RPiPlay sources.
# Requires mingw-w64: apt install gcc-mingw-w64-x86-64
set -e
x86_64-w64-mingw32-gcc -O2 -shared -static -static-libgcc \
  -o ../playfair.dll \
  playfair.c omg_hax.c hand_garble.c modified_md5.c sap_hash.c playfair.def -lm
echo "Built ../playfair.dll"
