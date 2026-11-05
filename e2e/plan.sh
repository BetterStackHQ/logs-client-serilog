#!/usr/bin/env bash
# Plans the E2E matrix: one job per leg, with the target framework, .NET SDK and package versions the leg tests.
# Prints the matrix as a JSON array, the include list of the test job in e2e.yml.
#   floor   .NET Framework 4.8 (the Windows target of the unit tests) with the oldest packages the client declares:
#           Serilog.Extensions.Logging, Serilog.Sinks.Http and Newtonsoft.Json as BetterStack.Logs.Serilog.csproj
#           references them, and Serilog as the unit tests' floor dependency set pins it
#   latest  the newest GA .NET SDK and its target framework, with the newest releases of those four packages
#   canary  the next .NET SDK as a preview (the newest GA one until the next preview is out), with a pre-release of
#           each package when one is newer than its newest release
# Every leg also pins Serilog.Sinks.Console, which only the example uses, to its newest release that accepts the leg's
# Serilog. The SDK that packs the client is the one release.yml packs with, in every leg.
# Env: LEG (all, floor, latest or canary), CLIENT_FROM (checkout or nuget), and SDK_VERSION, SERILOG_VERSION,
#      SINKS_HTTP_VERSION, EXTENSIONS_LOGGING_VERSION, NEWTONSOFT_VERSION: exact versions that replace the planned ones
#      in every leg.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
DOTNET_RELEASES=https://builds.dotnet.microsoft.com/dotnet/release-metadata
NUGET=https://api.nuget.org/v3
# Job names become artifact names (e2e-summary-<job name>), which must not contain any of these.
NOT_IN_NAMES='[:"<>|*?/\\]'

# A registry hiccup at schedule time should not file a Linear issue, so every lookup is retried.
fetch() { curl -fsS -m 60 --retry 3 --retry-delay 10 --retry-all-errors --compressed "$1"; }

# The version BetterStack.Logs.Serilog.csproj references package $1 at.
library_reference() { sed -n "s:.*<PackageReference Include=\"$1\" Version=\"\([^\"]*\)\".*:\1:p" "$ROOT/BetterStack.Logs.Serilog.csproj"; }
# The floor dependency set's value of property $1 in the unit tests' project: the exact version, not the latest set's 4.*.
test_floor() { sed -n "s:.*<$1 Condition=\"'\$($1)' == ''\">\([^<*]*\)</$1>.*:\1:p" "$ROOT/tests/BetterStack.Logs.Serilog.Tests/BetterStack.Logs.Serilog.Tests.csproj"; }

# Every version of NuGet package $1 on nuget.org, one per line.
versions() { fetch "$NUGET-flatcontainer/$(tr '[:upper:]' '[:lower:]' <<<"$1")/index.json" | jq -r '.versions[]'; }
# Whether version $1 sorts above version $2.
newer() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]; }
# The newest release among the versions on stdin: the highest one without a pre-release label such as -rc.1.
newest_release() { grep -v -- - | sort -V | tail -n 1; }
# The newest pre-release above the newest release among the versions on stdin, or that release when there is none.
newest_prerelease() {
  local all release candidate pick=""
  all=$(cat)
  release=$(newest_release <<<"$all")
  while read -r candidate; do
    # 4.4.1-dev-02453 is a pre-release of 4.4.1: it counts when 4.4.1 is above the newest release.
    if newer "${candidate%%-*}" "$release" && { [ -z "$pick" ] || newer "$candidate" "$pick"; }; then pick=$candidate; fi
  done < <(grep -- - <<<"$all")
  echo "${pick:-$release}"
}

# The newest Serilog.Sinks.Console release whose own Serilog dependency accepts Serilog $1: 6.x needs Serilog 4, so
# the floor's Serilog 2 gets an older one. From nuget.org's registration, whose pages hold each version's dependencies.
console_for_serilog() {
  local index pages
  index=$(fetch "$NUGET/registration5-gz-semver2/serilog.sinks.console/index.json")
  # Large registrations leave the versions out of the index and link a page for each range instead.
  pages=$(jq -c '.items[] | select(has("items"))' <<<"$index")
  while read -r page; do
    [ -z "$page" ] || pages+=$'\n'$(fetch "$page")
  done < <(jq -r '.items[] | select(has("items") | not) | ."@id"' <<<"$index")
  jq -r '.items[] | .catalogEntry | select(.version | contains("-") | not)
    | "\(.version) \([.dependencyGroups[]?.dependencies[]? | select(.id == "Serilog") | .range | ltrimstr("[") | sub("[,)].*"; "")] | first // "0")"' <<<"$pages" \
    | while read -r version needs; do
        # Serilog's own pre-release label does not matter here: 4.4.1-dev is a Serilog 4.4.1.
        if ! newer "$needs" "${1%%-*}"; then echo "$version"; fi
      done | sort -V | tail -n 1
}

# The SDK of the .NET release channel $1 (e.g. 10.0) that releases-index.json names as its newest.
channel_sdk() { jq -r --arg channel "$1" '.["releases-index"][] | select(."channel-version" == $channel) | ."latest-sdk"' <<<"$INDEX"; }
# The .NET runtime that ships with SDK $1, from its channel's releases.json.
sdk_runtime() {
  local channel
  channel=$(cut -d. -f1-2 <<<"$1")
  fetch "$DOTNET_RELEASES/$channel/releases.json" | jq -r --arg sdk "$1" 'first(.releases[] | select(any(.sdks[]?; .version == $sdk)) | .runtime.version) // empty'
}

INDEX=$(fetch "$DOTNET_RELEASES/releases-index.json")
# Channels whose newest SDK is a release (10.0) or a preview (11.0 while it has no GA).
GA_CHANNEL=$(jq -r '[.["releases-index"][] | select(."latest-sdk" | contains("-") | not) | ."channel-version"] | sort_by(split(".") | map(tonumber)) | last' <<<"$INDEX")
PREVIEW_CHANNEL=$(jq -r --arg ga "$GA_CHANNEL" '[.["releases-index"][] | select((."latest-sdk" | contains("-")) and (."channel-version" | split(".") | map(tonumber)) > ($ga | split(".") | map(tonumber))) | ."channel-version"] | sort_by(split(".") | map(tonumber)) | last // empty' <<<"$INDEX")
# release.yml installs one channel, e.g. dotnet-version: 10.0.x, and packs with its newest SDK.
PACK_CHANNEL=$(sed -n 's/^ *dotnet-version: *\([0-9]*\.[0-9]*\)\.x *$/\1/p' "$ROOT/.github/workflows/release.yml" | head -n 1)
PACK_SDK=$(channel_sdk "$PACK_CHANNEL")
# The .NET Framework the unit tests run on under Windows.
FLOOR_TFM=$(grep -o 'net4[0-9]*' "$ROOT/tests/BetterStack.Logs.Serilog.Tests/BetterStack.Logs.Serilog.Tests.csproj" | head -n 1)

CLIENT=0.0.0-e2e
if [ "${CLIENT_FROM:-checkout}" = nuget ]; then CLIENT=$(versions BetterStack.Logs.Serilog | newest_release); fi

LEGS=$([ "${LEG:-all}" = all ] && echo "floor latest canary" || echo "$LEG")
MATRIX='[]'
for leg in $LEGS; do
  case "$leg" in
    floor)
      sdk=$PACK_SDK
      serilog=$(test_floor SerilogVersion)
      sinks_http=$(library_reference Serilog.Sinks.Http)
      extensions_logging=$(library_reference Serilog.Extensions.Logging)
      newtonsoft=$(library_reference Newtonsoft.Json) ;;
    latest)
      sdk=$(channel_sdk "$GA_CHANNEL")
      serilog=$(versions Serilog | newest_release)
      sinks_http=$(versions Serilog.Sinks.Http | newest_release)
      extensions_logging=$(versions Serilog.Extensions.Logging | newest_release)
      newtonsoft=$(versions Newtonsoft.Json | newest_release) ;;
    canary)
      sdk=$(channel_sdk "${PREVIEW_CHANNEL:-$GA_CHANNEL}")
      serilog=$(versions Serilog | newest_prerelease)
      sinks_http=$(versions Serilog.Sinks.Http | newest_prerelease)
      extensions_logging=$(versions Serilog.Extensions.Logging | newest_prerelease)
      newtonsoft=$(versions Newtonsoft.Json | newest_prerelease) ;;
    *) echo "unknown leg $leg" >&2; exit 1 ;;
  esac
  sdk=${SDK_VERSION:-$sdk}
  serilog=${SERILOG_VERSION:-$serilog}
  sinks_http=${SINKS_HTTP_VERSION:-$sinks_http}
  extensions_logging=${EXTENSIONS_LOGGING_VERSION:-$extensions_logging}
  newtonsoft=${NEWTONSOFT_VERSION:-$newtonsoft}
  if [ "$leg" = floor ]; then
    tfm=$FLOOR_TFM
    # net48 is .NET Framework 4.8, net472 is 4.7.2.
    runtime=".NET Framework $(sed 's/\(.\)/\1./g; s/\.$//' <<<"${FLOOR_TFM#net}")"
  else
    # The example targets the SDK's own framework, so that it runs on the runtime this SDK brings.
    tfm=net$(cut -d. -f1-2 <<<"$sdk")
    runtime=$(sdk_runtime "$sdk")
  fi
  console=$(console_for_serilog "$serilog")
  for value in "$PACK_SDK" "$sdk" "$tfm" "$runtime" "$serilog" "$sinks_http" "$extensions_logging" "$newtonsoft" "$console" "$CLIENT"; do
    [ -n "$value" ] && [ "$value" != null ] || { echo "the $leg leg could not be planned: pack SDK '$PACK_SDK', SDK '$sdk', framework '$tfm', runtime '$runtime', Serilog '$serilog', Serilog.Sinks.Http '$sinks_http', Serilog.Extensions.Logging '$extensions_logging', Newtonsoft.Json '$newtonsoft', Serilog.Sinks.Console '$console', client '$CLIENT'" >&2; exit 1; }
  done
  if [ "$leg" = canary ]; then
    name="canary ($tfm, SDK $sdk, newest pre-releases of Serilog, Serilog.Sinks.Http, Serilog.Extensions.Logging and Newtonsoft.Json)"
  else
    name="$leg ($tfm, SDK $sdk, Serilog $serilog, Serilog.Sinks.Http $sinks_http, Serilog.Extensions.Logging $extensions_logging, Newtonsoft.Json $newtonsoft)"
  fi
  if grep -q "$NOT_IN_NAMES" <<<"$name"; then echo "job name '$name' contains one of $NOT_IN_NAMES, which artifact names do not allow" >&2; exit 1; fi
  MATRIX=$(jq -c --arg leg "$leg" --arg name "$name" --arg tfm "$tfm" --arg sdk "$sdk" --arg pack_sdk "$PACK_SDK" \
    --arg runtime "$runtime" --arg serilog "$serilog" --arg sinks_http "$sinks_http" --arg extensions_logging "$extensions_logging" \
    --arg newtonsoft "$newtonsoft" --arg console "$console" --arg source "${CLIENT_FROM:-checkout}" --arg client "$CLIENT" '. + [{
      leg: $leg, name: $name, tfm: $tfm, sdk: $sdk, pack_sdk: $pack_sdk, runtime: $runtime,
      serilog: $serilog, sinks_http: $sinks_http, extensions_logging: $extensions_logging, newtonsoft: $newtonsoft,
      console: $console, source: $source, client: $client,
      # .NET Framework needs Windows. The other legs follow ubuntu-latest, so a new runner image is covered as well.
      runner: (if ($tfm | startswith("net4")) then "windows-latest" else "ubuntu-latest" end)
    }]' <<<"$MATRIX")
done
echo "$MATRIX"
