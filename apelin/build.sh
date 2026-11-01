#!/bin/sh
set -eu

: "${APELIN_PACKAGE_DIR:?run this through apelin_project_build}"
: "${APELIN_PROJECT_ROOT:?}" "${APELIN_BOOTSTRAP:?}" "${APELIN_ARCHITECTURE:?}"
: "${APELIN_BUILD_ID:?}" "${TMPDIR:?}"
: "${APELIN_PREFIX?}"

stage="$(mktemp -d "${TMPDIR:?}dualcopy-stage.XXXXXX")"
trap 'rm -rf -- "${stage:?}"' EXIT

mkdir -p "${stage:?}/DEBIAN"
mkdir -p "${stage:?}${APELIN_PREFIX}/Library/MobileSubstrate/DynamicLibraries"

arch="${APELIN_ARCHITECTURE}"
case "${arch}" in
  iphoneos-arm64e) clang_arch="arm64e" ;;
  iphoneos-arm64) clang_arch="arm64" ;;
  *) echo "unsupported architecture: ${arch}" >&2; exit 2 ;;
esac

src="${APELIN_PROJECT_ROOT:?}/Sources/DualCopyTweak.m"
out="${stage:?}/DualCopyTweak.o"
dylib="${stage:?}/DualCopy.dylib"

if [ "${APELIN_BOOTSTRAP}" = "roothide" ]; then
  bootstrap_define=-DAPELIN_BOOTSTRAP_ROOTHIDE=1
else
  bootstrap_define=-DAPELIN_BOOTSTRAP_ROOTLESS=1
fi

clang -fobjc-arc -fvisibility=hidden -Werror -Werror=unguarded-availability-new \
  -isysroot "${SDKROOT:?}" -arch "${clang_arch}" -miphoneos-version-min=16.0 \
  -I "${APELIN_PROJECT_ROOT:?}/Sources" \
  -DAPELIN_BUILD_ID="\"${APELIN_BUILD_ID}\"" \
  -DAPELIN_PACKAGE_ID="\"local.apelin.dualcopy\"" \
  -DAPELIN_VERSION="\"0.1.0\"" \
  -DAPELIN_PRODUCT_NAME="\"DualCopy\"" \
  "${bootstrap_define}" \
  -c "${src:?}" -o "${out:?}"

clang -dynamiclib -fobjc-arc -fvisibility=hidden -isysroot "${SDKROOT:?}" \
  -arch "${clang_arch}" -miphoneos-version-min=16.0 \
  -install_name @rpath/DualCopy.dylib -o "${dylib:?}" "${out:?}" \
  -framework Foundation -framework UIKit

ldid -S "${dylib:?}"
cp "${dylib:?}" "${stage:?}${APELIN_PREFIX}/Library/MobileSubstrate/DynamicLibraries/DualCopy.dylib"

cat > "${stage:?}${APELIN_PREFIX}/Library/MobileSubstrate/DynamicLibraries/DualCopy.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Filter</key>
  <dict>
    <key>Bundles</key>
    <array>
      <string>com.apple.springboard</string>
    </array>
  </dict>
</dict>
</plist>
PLIST

cat > "${stage:?}/DEBIAN/control" <<CONTROL
Package: local.apelin.dualcopy
Name: DualCopy
Version: 0.1.0
Architecture: ${arch}
Description: Share clipboard history between SpringBoard and a Windows 11 companion over TCP.
Maintainer: DualCopy contributors
Author: DualCopy contributors
Section: Tweaks
Depends: mobilesubstrate
CONTROL

dpkg-deb -b --root-owner-group "${stage:?}" "${APELIN_PACKAGE_DIR:?}/"
