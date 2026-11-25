%global debug_package %{nil}
%global __provides_exclude_from ^%{_libdir}/%{name}/.*\.so$

Name:           kashishu
Version:        0.1.20
Release:        1.0%{?dist}
Summary:        Desktop lyrics island for Spotify and MPRIS
License:        Apache-2.0 AND GPL-3.0-or-later AND MIT AND BSD-3-Clause
URL:            https://github.com/xiaozhangup/Kashishu
Source0:        %{name}-%{version}.tar.gz
Source1:        %{name}-nuget-%{version}.tar
BuildRequires:  dotnet-sdk-10.0
BuildRequires:  gcc
BuildRequires:  opencc-devel
BuildRequires:  python3
Requires:       dotnet-runtime-10.0
Requires:       libwayland-client
Requires:       libglvnd-egl
Requires:       opencc
Provides:       lyricify-island = %{version}-%{release}
Obsoletes:      lyricify-island < 0.1.14
ExclusiveArch:  x86_64

%description
Wayland desktop lyrics island for Spotify and local MPRIS players.

%prep
%autosetup
tar -xf %{SOURCE1}
python3 packaging/apply-copr-helper.py .

%build
export DOTNET_CLI_HOME="$PWD/.dotnet-home"
export NUGET_PACKAGES="$PWD/.nuget-packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dotnet restore Kashishu.csproj --runtime fedora.%{fedora}-x64 --source "$PWD/nuget-feed" -p:SelfContained=false -p:NuGetAudit=false
dotnet publish Kashishu.csproj --configuration Release --runtime fedora.%{fedora}-x64 --self-contained false --no-restore -p:DebugType=None --output "$PWD/publish"
gcc $RPM_OPT_FLAGS $RPM_LD_FLAGS -shared -fPIC packaging/opencc/shim.c -o publish/liblyricify-opencc.so -lopencc

%install
install -dm755 %{buildroot}%{_libdir}/%{name} %{buildroot}%{_bindir}
cp -a publish/. %{buildroot}%{_libdir}/%{name}/
ln -s ../%{_lib}/%{name}/Kashishu %{buildroot}%{_bindir}/Kashishu
install -Dm644 packaging/kashishu.desktop %{buildroot}%{_datadir}/applications/kashishu.desktop
install -Dm644 Resources/Icons/kashishu.png %{buildroot}%{_datadir}/icons/hicolor/128x128/apps/kashishu.png
install -Dm644 Resources/Icons/kashishu.svg %{buildroot}%{_datadir}/icons/hicolor/scalable/apps/kashishu.svg
install -Dm644 LICENSE %{buildroot}%{_licensedir}/%{name}/Kashishu-Apache-2.0.txt
install -Dm644 vendor/Lyricify-Lyrics-Helper/LICENSE %{buildroot}%{_licensedir}/%{name}/Lyricify-Lyrics-Helper-Apache-2.0.txt
install -Dm644 licenses/NWayland-MIT.txt %{buildroot}%{_licensedir}/%{name}/NWayland-MIT.txt
install -Dm644 licenses/OpenAI-MIT.txt %{buildroot}%{_licensedir}/%{name}/OpenAI-MIT.txt
install -Dm644 licenses/Microsoft-MIT.txt %{buildroot}%{_licensedir}/%{name}/Microsoft-MIT.txt
install -Dm644 licenses/OppoPodsManager-GPL-3.0.txt %{buildroot}%{_licensedir}/%{name}/OppoPodsManager-GPL-3.0.txt
python3 packaging/write-nuget-licenses.py .nuget-packages "%{buildroot}%{_licensedir}/%{name}/nuget" licenses/NWayland-MIT.txt
install -Dm644 THIRD_PARTY_NOTICES.md %{buildroot}%{_docdir}/%{name}/THIRD_PARTY_NOTICES.md

%files
%license %{_licensedir}/%{name}/
%doc %{_docdir}/%{name}/
%{_bindir}/Kashishu
%{_libdir}/%{name}/
%{_datadir}/applications/kashishu.desktop
%{_datadir}/icons/hicolor/128x128/apps/kashishu.png
%{_datadir}/icons/hicolor/scalable/apps/kashishu.svg
