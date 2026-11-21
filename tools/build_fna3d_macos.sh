#!/usr/bin/env sh
# Builds OpenMagicka's patched FNA3D for macOS arm64 (native/README.md):
# lib/FNA/lib/FNA3D + MojoShader + native/patches/MojoShader/*.patch -> native/osx-arm64/libFNA3D.0.dylib.
# Needs clang, cmake, git, and lib/fnalibs/osx/libSDL3.0.dylib (from tools/fetch_fnalibs.sh).
set -eu

SDL_VERSION="3.4.18"
ROOT=$(cd "$(dirname "$0")/.." && pwd)
SOURCE="$ROOT/lib/FNA/lib/FNA3D"
PATCH_DIR="$ROOT/native/patches/MojoShader"
MACOS_PATCH_DIR="$ROOT/native/patches/MojoShader-macos"
FNA3D_MACOS_PATCH_DIR="$ROOT/native/patches/FNA3D-macos"
OUTPUT="$ROOT/native/osx-arm64"
SDL_DYLIB="$ROOT/lib/fnalibs/osx/libSDL3.0.dylib"

if [ ! -f "$SOURCE/MojoShader/mojoshader.c" ]; then
    echo "FNA3D or MojoShader sources are missing: run git submodule update --init --recursive" >&2
    exit 1
fi
if [ ! -f "$SDL_DYLIB" ]; then
    echo "Missing $SDL_DYLIB: run tools/fetch_fnalibs.sh first" >&2
    exit 1
fi

WORK=$(mktemp -d "${TMPDIR:-/tmp}/openmagicka-fna3d-macos.XXXXXX")
trap 'rm -rf "$WORK"' EXIT

echo "Downloading SDL3-$SDL_VERSION headers..."
SDL_TAR="$WORK/SDL3-$SDL_VERSION.tar.gz"
curl -fsSL "https://github.com/libsdl-org/SDL/releases/download/release-$SDL_VERSION/SDL3-$SDL_VERSION.tar.gz" -o "$SDL_TAR"
tar -xzf "$SDL_TAR" -C "$WORK" "SDL3-$SDL_VERSION/include"
SDL_INCLUDE="$WORK/SDL3-$SDL_VERSION/include"

echo "Copying FNA3D sources and applying MojoShader patches..."
SRC="$WORK/src"
mkdir -p "$SRC"
(cd "$SOURCE" && tar --exclude=.git -cf - .) | tar -C "$SRC" -xf -

for patch in "$PATCH_DIR"/*.patch "$MACOS_PATCH_DIR"/*.patch; do
    [ -f "$patch" ] || continue
    echo "  Applying $(basename "$patch")"
    git -C "$SRC/MojoShader" apply --ignore-whitespace "$patch"
done

# Verify every patched file carries the OpenMagicka fork marker
for patch in "$PATCH_DIR"/*.patch "$MACOS_PATCH_DIR"/*.patch; do
    [ -f "$patch" ] || continue
    for rel in $(grep -E '^\+\+\+ b/' "$patch" | sed 's|^\+\+\+ b/||'); do
        if ! grep -q "OpenMagicka fork" "$SRC/MojoShader/$rel"; then
            echo "Patch $(basename "$patch") did not reach $rel" >&2
            exit 1
        fi
    done
done

for patch in "$FNA3D_MACOS_PATCH_DIR"/*.patch; do
    [ -f "$patch" ] || continue
    echo "  Applying FNA3D/$(basename "$patch")"
    git -C "$SRC" apply --ignore-whitespace "$patch"
done

BUILD="$WORK/build"
echo "Configuring FNA3D for macOS arm64..."
cmake -S "$SRC" -B "$BUILD" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 \
    -DBUILD_SDL3=ON \
    -DSDL3_INCLUDE_DIRS="$SDL_INCLUDE" \
    -DSDL3_LIBRARIES="$SDL_DYLIB"

echo "Building FNA3D..."
cmake --build "$BUILD" --parallel

BUILT="$BUILD/libFNA3D.0.dylib"
if [ ! -f "$BUILT" ]; then
    BUILT=$(ls "$BUILD"/libFNA3D*.dylib | head -1)
fi

mkdir -p "$OUTPUT"
cp -f "$BUILT" "$OUTPUT/libFNA3D.0.dylib"
install_name_tool -id "@rpath/libFNA3D.0.dylib" "$OUTPUT/libFNA3D.0.dylib"
if ! otool -l "$OUTPUT/libFNA3D.0.dylib" | grep -q "@loader_path"; then
    install_name_tool -add_rpath "@loader_path" "$OUTPUT/libFNA3D.0.dylib"
fi
codesign --force --sign - "$OUTPUT/libFNA3D.0.dylib"

FNA3D_REV=$(git -C "$SOURCE" rev-parse HEAD)
MOJO_REV=$(git -C "$SOURCE/MojoShader" rev-parse HEAD)
COMPILER=$(cc --version | head -1)
LIB_HASH=$(shasum -a 256 "$OUTPUT/libFNA3D.0.dylib" | awk '{print $1}')

{
    echo "OpenMagicka build of FNA3D (altered source: see native/README.md)."
    echo "FNA3D $FNA3D_REV"
    echo "MojoShader $MOJO_REV"
    echo "SDL3 headers $SDL_VERSION"
    echo "Compiler: $COMPILER (arm64)"
    echo "Patches:"
    for patch in "$PATCH_DIR"/*.patch; do
        ph=$(shasum -a 256 "$patch" | awk '{print $1}')
        echo "  MojoShader/$(basename "$patch") sha256 $ph"
    done
    for patch in "$MACOS_PATCH_DIR"/*.patch "$FNA3D_MACOS_PATCH_DIR"/*.patch; do
        [ -f "$patch" ] || continue
        ph=$(shasum -a 256 "$patch" | awk '{print $1}')
        echo "  $(basename "$(dirname "$patch")")/$(basename "$patch") sha256 $ph"
    done
    echo "libFNA3D.0.dylib sha256 $LIB_HASH"
} > "$OUTPUT/SOURCE.txt"

echo "Built $OUTPUT/libFNA3D.0.dylib"
otool -L "$OUTPUT/libFNA3D.0.dylib"
cat "$OUTPUT/SOURCE.txt"
