#!/bin/sh
set -eu

: "${APELIN_PACKAGE_DIR:?run this through apelin_project_build}"
: "${APELIN_PROJECT_ROOT:?}" "${APELIN_BOOTSTRAP:?}" "${APELIN_ARCHITECTURE:?}"
: "${APELIN_BUILD_ID:?}" "${TMPDIR:?}" "${APELIN_TOOLCHAIN_BIN:?}"
: "${APELIN_PREFIX?}"

stage="$(mktemp -d "${TMPDIR:?}dualcopy-stage.XXXXXX")"
trap 'rm -rf -- "${stage:?}"' EXIT

package_id="com.eliastekdin.dualcopy"
version="0.3.6"
bootstrap_root="${APELIN_TOOLCHAIN_BIN:?}"
bootstrap_root="${bootstrap_root%/usr/lib/apelin/toolchains/*}"
support_root="${bootstrap_root:?}/usr/share/apelin/development/wiki.qaq.apelin.dev.preferences/support"

mkdir -p "${stage:?}/DEBIAN"
mkdir -p "${stage:?}${APELIN_PREFIX}/Library/MobileSubstrate/DynamicLibraries"
mkdir -p "${stage:?}${APELIN_PREFIX}/Library/PreferenceBundles/DualCopyPrefs.bundle"
mkdir -p "${stage:?}${APELIN_PREFIX}/Library/PreferenceLoader/Preferences"

arch="${APELIN_ARCHITECTURE}"
case "${arch}" in
  iphoneos-arm64e) clang_arch="arm64e" ;;
  iphoneos-arm64) clang_arch="arm64" ;;
  *) echo "unsupported architecture: ${arch}" >&2; exit 2 ;;
esac

if [ "${APELIN_BOOTSTRAP}" = "roothide" ]; then
  bootstrap_define=-DAPELIN_BOOTSTRAP_ROOTHIDE=1
else
  bootstrap_define=-DAPELIN_BOOTSTRAP_ROOTLESS=1
fi

common_flags="-isysroot ${SDKROOT:?} -arch ${clang_arch} -miphoneos-version-min=16.0 -fvisibility=hidden -Werror=unguarded-availability-new"
tweak_object="${stage:?}/DualCopyTweak.o"
tweak_dylib="${stage:?}/DualCopy.dylib"
prefs_object="${stage:?}/DualCopyPrefs.o"
prefs_bundle="${stage:?}${APELIN_PREFIX}/Library/PreferenceBundles/DualCopyPrefs.bundle"

clang -fobjc-arc ${common_flags} -I "${APELIN_PROJECT_ROOT:?}/Sources" \
  -DAPELIN_BUILD_ID="\"${APELIN_BUILD_ID}\"" \
  -DAPELIN_PACKAGE_ID="\"${package_id}\"" \
  -DAPELIN_VERSION="\"${version}\"" \
  -DAPELIN_PRODUCT_NAME="\"DualCopy\"" "${bootstrap_define}" \
  -c "${APELIN_PROJECT_ROOT:?}/Sources/DualCopyTweak.m" -o "${tweak_object:?}"

clang -dynamiclib -fobjc-arc ${common_flags} \
  -install_name @rpath/DualCopy.dylib -o "${tweak_dylib:?}" "${tweak_object:?}" \
  -framework Foundation -framework UIKit
ldid -S "${tweak_dylib:?}"
cp "${tweak_dylib:?}" "${stage:?}${APELIN_PREFIX}/Library/MobileSubstrate/DynamicLibraries/DualCopy.dylib"
if [ "${APELIN_BOOTSTRAP}" = "roothide" ]; then
  ln -s /usr/lib/DynamicPatches/AutoPatches.dylib \
    "${stage:?}${APELIN_PREFIX}/Library/MobileSubstrate/DynamicLibraries/DualCopy.dylib.roothidepatch"
fi

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

clang -fobjc-arc ${common_flags} -I "${support_root:?}/include" -F "${support_root:?}/stubs" \
  -c "${APELIN_PROJECT_ROOT:?}/Preferences/Sources/DualCopyPrefs.m" -o "${prefs_object:?}"
clang -bundle -fobjc-arc ${common_flags} -F "${support_root:?}/stubs" \
  -o "${prefs_bundle:?}/DualCopyPrefs" "${prefs_object:?}" \
  -framework Foundation -framework UIKit -framework Preferences
ldid -S "${prefs_bundle:?}/DualCopyPrefs"
cp "${APELIN_PROJECT_ROOT:?}/Preferences/Resources/Info.plist" "${prefs_bundle:?}/Info.plist"
cp "${APELIN_PROJECT_ROOT:?}/Preferences/Resources/Root.plist" "${prefs_bundle:?}/Root.plist"
cp "${APELIN_PROJECT_ROOT:?}/Preferences/Packaging/entry.plist" \
   "${stage:?}${APELIN_PREFIX}/Library/PreferenceLoader/Preferences/DualCopyPrefs.plist"

cat > "${stage:?}/DEBIAN/control" <<CONTROL
Package: ${package_id}
Name: DualCopy
Version: ${version}
Architecture: ${arch}
Description: Share text, images, raw camera data and files between SpringBoard and a Windows 11 companion over TCP.
Maintainer: DualCopy contributors
Author: DualCopy contributors
Section: Tweaks
Depends: mobilesubstrate, preferenceloader
CONTROL

dpkg-deb -b --root-owner-group "${stage:?}" "${APELIN_PACKAGE_DIR:?}/"
