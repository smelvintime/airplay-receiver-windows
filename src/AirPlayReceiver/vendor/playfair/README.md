# Vendored: playfair (FairPlay key-unwrap)

`playfair.dll` performs Apple's FairPlay key-unwrap — given the 164-byte FairPlay
key message (the `fp-setup` phase-2 request) and the 72-byte encrypted stream key
(`ekey` from `SETUP`), it returns the 16-byte AES key used to decrypt the mirror
video stream. The receiver calls it via P/Invoke (`Protocol/Playfair.cs`).

## Origin

The C sources under `src/` are taken verbatim from **RPiPlay** `lib/playfair`
(https://github.com/FD-/RPiPlay, GPLv3) — the well-known community implementation
of the FairPlay SAP key derivation. They are vendored here for transparency and
so the DLL can be rebuilt.

## Prebuilt DLL

`playfair.dll` is checked in prebuilt (x86-64) so the app needs no native build
tools. It is cross-compiled with mingw-w64 and links only the always-present
`KERNEL32.dll` / `msvcrt.dll`, so it loads on any 64-bit Windows.

## Rebuilding

On a machine with `mingw-w64` (e.g. `apt install gcc-mingw-w64-x86-64`):

```sh
cd src && ./build.sh
```

or with MSVC on Windows (Developer Command Prompt):

```bat
cl /O2 /LD playfair.c omg_hax.c hand_garble.c modified_md5.c sap_hash.c /Fe:..\playfair.dll
```
