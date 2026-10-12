#!/usr/bin/env bash
# Packs Crypto.Net with a local version, installs the templates into a private hive and checks that every
# template instantiates, restores against the local packages, builds and (where it has tests) passes.
# Usage: scripts/test-templates.sh [work-dir]
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
work="${1:-$(mktemp -d)}"
version="1.1.0-local.$(date +%s)"
feed="$work/feed"
hive="$work/hive"
mkdir -p "$feed" "$hive"

echo "== pack $version into $feed"
dotnet pack "$repo/Crypto.Net.slnx" -c Release -o "$feed" -p:Version="$version" -p:IncludeSymbols=false --nologo -v q
dotnet pack "$repo/templates/Crypto.Net.Templates.csproj" -c Release -o "$feed" -p:Version="$version" --nologo -v q

dotnet new install "$feed/Crypto.Net.Templates.$version.nupkg" --debug:custom-hive "$hive" > /dev/null

cat > "$work/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

for template in cnet-console cnet-worker cnet-webapi cnet-lib cnet-test ${EXTRA_TEMPLATES:-}; do
  out="$work/apps/$template"
  echo "== $template"
  dotnet new "$template" -n Sample -o "$out" --CryptoNetVersion "$version" --skipRestore --debug:custom-hive "$hive" > /dev/null
  cp "$work/nuget.config" "$out/"
  (cd "$out" && dotnet build --nologo -v q -warnaserror:CNET001,CNET003,NU1901,NU1902,NU1903,NU1904 -clp:ErrorsOnly)
  case "$template" in
    cnet-lib|cnet-test) (cd "$out" && dotnet test --nologo -v q --no-build) ;;
  esac
done
echo "All templates OK"
