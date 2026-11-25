#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
version="${1:-0.1.18}"
topdir="$(mktemp -d)"
trap 'rm -rf -- "$topdir"' EXIT

command -v rpmbuild >/dev/null || { echo 'rpmbuild is required' >&2; exit 1; }
[[ "$version" =~ ^[0-9][0-9A-Za-z._+~]*$ ]] || { echo "invalid RPM version: $version" >&2; exit 1; }

"$project_dir/build.sh"
mkdir -p "$topdir"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
install -m755 "$project_dir/dist/Kashishu" "$topdir/SOURCES/Kashishu"
install -m644 "$project_dir/dist/THIRD_PARTY_NOTICES.md" "$topdir/SOURCES/THIRD_PARTY_NOTICES.md"
install -m644 "$project_dir/dist/licenses/"*.txt "$topdir/SOURCES/"
install -m644 "$project_dir/packaging/kashishu.desktop" "$topdir/SOURCES/kashishu.desktop"
install -m644 "$project_dir/Resources/Icons/kashishu.png" "$topdir/SOURCES/kashishu.png"
install -m644 "$project_dir/Resources/Icons/kashishu.svg" "$topdir/SOURCES/kashishu.svg"

cat >"$topdir/SPECS/kashishu.spec" <<EOF
%global __strip /bin/true
Name:           kashishu
Version:        $version
Release:        1%{?dist}
Summary:        Desktop lyrics island for Spotify and MPRIS
License:        MIT AND Apache-2.0 AND GPL-3.0-or-later
URL:            https://github.com/xiaozhangup/Kashishu
BuildArch:      x86_64
Requires:       libwayland-client, libglvnd-egl
Provides:       lyricify-island = %{version}-%{release}
Obsoletes:      lyricify-island < 0.1.14

%description
Wayland desktop lyrics island for Spotify and local MPRIS players.

%install
install -Dm755 %{_sourcedir}/Kashishu %{buildroot}%{_bindir}/Kashishu
install -Dm644 %{_sourcedir}/kashishu.desktop %{buildroot}%{_datadir}/applications/kashishu.desktop
install -Dm644 %{_sourcedir}/kashishu.png %{buildroot}%{_datadir}/icons/hicolor/128x128/apps/kashishu.png
install -Dm644 %{_sourcedir}/kashishu.svg %{buildroot}%{_datadir}/icons/hicolor/scalable/apps/kashishu.svg

install -Dm644 %{_sourcedir}/THIRD_PARTY_NOTICES.md %{buildroot}%{_docdir}/%{name}/THIRD_PARTY_NOTICES.md
install -Dm644 %{_sourcedir}/Kashishu-Apache-2.0.txt %{buildroot}%{_licensedir}/%{name}/Kashishu-Apache-2.0.txt
install -Dm644 %{_sourcedir}/Lyricify-Lyrics-Helper-Apache-2.0.txt %{buildroot}%{_licensedir}/%{name}/Lyricify-Lyrics-Helper-Apache-2.0.txt
install -Dm644 %{_sourcedir}/OppoPodsManager-GPL-3.0.txt %{buildroot}%{_licensedir}/%{name}/OppoPodsManager-GPL-3.0.txt
install -Dm644 %{_sourcedir}/NWayland-MIT.txt %{buildroot}%{_licensedir}/%{name}/NWayland-MIT.txt
install -Dm644 %{_sourcedir}/OpenAI-MIT.txt %{buildroot}%{_licensedir}/%{name}/OpenAI-MIT.txt
install -Dm644 %{_sourcedir}/Microsoft-MIT.txt %{buildroot}%{_licensedir}/%{name}/Microsoft-MIT.txt

%files
%doc %{_docdir}/%{name}/THIRD_PARTY_NOTICES.md
%license %{_licensedir}/%{name}/*.txt
%{_bindir}/Kashishu
%{_datadir}/applications/kashishu.desktop
%{_datadir}/icons/hicolor/128x128/apps/kashishu.png
%{_datadir}/icons/hicolor/scalable/apps/kashishu.svg
EOF

rpmbuild -bb --define "_topdir $topdir" "$topdir/SPECS/kashishu.spec"
mkdir -p "$project_dir/dist"
find "$topdir/RPMS" -name '*.rpm' -exec install -m644 {} "$project_dir/dist/" \;
printf 'Built %s\n' "$project_dir"/dist/*.rpm
