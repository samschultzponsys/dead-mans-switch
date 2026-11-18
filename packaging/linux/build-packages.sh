#!/usr/bin/env bash
# Builds the Debian (.deb) and Fedora (.rpm) packages from a linux-x64 publish:
#
#   dotnet publish src/DeadMansSwitch/DeadMansSwitch.csproj -c Release -r linux-x64 -o dist/linux-x64
#   packaging/linux/build-packages.sh "$(cat VERSION)" dist/linux-x64 dist
#
# Needs dpkg-deb and rpmbuild (on Debian/Ubuntu: apt install rpm).
# Both packages install the same files:
#   /usr/lib/dead-mans-switch/   the app (self-contained, no .NET install needed)
#   /usr/bin/dead-mans-switch    command, linking to it
#   plus the desktop entry, icons and AppStream metadata.
set -euo pipefail

version=$1
publish=$(realpath "$2")
out=$(realpath -m "$3")
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
id=io.github.samschultzponsys.DeadMansSwitch
name=dead-mans-switch
date=$(date -u +%F)

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

# The installed file tree, shared by both formats.
tree=$work/tree
install -d "$tree/usr/lib/$name" "$tree/usr/bin" "$tree/usr/share/applications" \
           "$tree/usr/share/metainfo" "$tree/usr/share/doc/$name"
install -m755 "$publish/DeadMansSwitch" "$tree/usr/lib/$name/"
install -m644 "$publish"/*.so "$tree/usr/lib/$name/"
ln -s "../lib/$name/DeadMansSwitch" "$tree/usr/bin/$name"
install -m644 "$here/$id.desktop" "$tree/usr/share/applications/"
sed -e "s/@VERSION@/$version/" -e "s/@DATE@/$date/" "$here/$id.metainfo.xml" > "$tree/usr/share/metainfo/$id.metainfo.xml"
for size in 16 24 32 48 64 128 256 512; do
  install -Dm644 "$root/assets/icons/$size.png" "$tree/usr/share/icons/hicolor/${size}x${size}/apps/$id.png"
done
install -m644 "$root/assets/fonts/OFL.txt" "$tree/usr/share/doc/$name/OFL-Inter.txt"
summary="Shuts the PC down unless someone keeps confirming they're there"
description="A countdown asks you to confirm you're still there. If nobody does, a
final warning pops up, and then the PC shuts down, restarts or hibernates."

# ----- .deb -----
deb=$work/deb
cp -a "$tree" "$deb"
install -d "$deb/DEBIAN"
cat > "$deb/DEBIAN/control" <<EOF
Package: $name
Version: $version
Architecture: amd64
Maintainer: samschultzponsys <samschultzponsys@users.noreply.github.com>
Installed-Size: $(du -sk "$tree" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, libfontconfig1, libx11-6, libice6, libsm6, libxrandr2, libxi6, libxcursor1, libssl3t64 | libssl3, ca-certificates
Recommends: pkexec | policykit-1, pipewire-bin | pulseaudio-utils | alsa-utils
Section: utils
Priority: optional
Homepage: https://github.com/samschultzponsys/dead-mans-switch
Description: $summary
$(echo "$description" | sed 's/^/ /')
EOF
dpkg-deb --root-owner-group --build "$deb" "$out/${name}_${version}_amd64.deb"

# ----- .rpm -----
# The app is already built, so the spec only copies the tree. rpmbuild must not
# strip it: stripping corrupts a .NET single-file app.
cat > "$work/$name.spec" <<EOF
%global debug_package %{nil}
%global __strip /bin/true
%global __os_install_post %{nil}
%global _build_id_links none

Name:           $name
Version:        $version
Release:        1
Summary:        $summary
License:        Proprietary
URL:            https://github.com/samschultzponsys/dead-mans-switch
BuildArch:      x86_64
AutoReqProv:    no
Requires:       fontconfig libX11 libICE libSM libXrandr libXi libXcursor openssl-libs ca-certificates
Recommends:     polkit pipewire-utils

%description
$description

%install
cp -a "$tree/." %{buildroot}/

%files
/usr/lib/$name
/usr/bin/$name
/usr/share/applications/$id.desktop
/usr/share/metainfo/$id.metainfo.xml
/usr/share/icons/hicolor/*/apps/$id.png
%doc /usr/share/doc/$name
EOF
rpmbuild --quiet -bb "$work/$name.spec" --define "_topdir $work/rpm" --define "_rpmdir $out" \
  --define "_build_name_fmt %%{NAME}-%%{VERSION}-%%{RELEASE}.%%{ARCH}.rpm"

ls -l "$out/${name}_${version}_amd64.deb" "$out/$name-$version-1.x86_64.rpm"
