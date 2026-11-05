#!/usr/bin/env bash
# Packs the client and builds and runs example-project/ the way its README tells a customer to (`dotnet run`), with
# the leg's target framework, .NET SDK and package versions. A failure ends with one ::error:: line giving the reason.
#   e2e/example.sh pack       packs the client as release.yml does, as version 0.0.0-e2e, into artifacts/
#   e2e/example.sh install    points the example at the leg's framework, SDK, client and packages, and restores it
#   e2e/example.sh versions   checks that the SDK, runtime, client and packages are the planned ones, into out/versions.md
#   e2e/example.sh build      builds the example and, on a checkout run, checks that it took the packed client
#   e2e/example.sh run        runs the example, which sends its logs to BETTER_STACK_INGESTING_HOST
# Env: the leg's matrix entry from e2e/plan.sh, as LEG, TFM, SDK, PACK_SDK, RUNTIME, CLIENT_FROM (checkout or nuget),
#      CLIENT, SERILOG, SINKS_HTTP, EXTENSIONS_LOGGING, NEWTONSOFT and CONSOLE.
set -euo pipefail
# A native jq.exe, as on Windows runners, ends its output lines with CRLF unless given -b.
if [ "${OS:-}" = Windows_NT ]; then jq() { command jq -b "$@"; }; fi

cd "$(dirname "$0")/.."
mkdir -p out
ID=BetterStack.Logs.Serilog
NUGET_ORG=https://api.nuget.org/v3/index.json
ASSETS=example-project/obj/project.assets.json
# The example restores into a folder of its own instead of the shared NuGet cache, so every package comes from a
# source during this job and the checks find each one at a known path.
PACKAGES=out/packages
WHAT="the example on $TFM with Serilog $SERILOG, Serilog.Sinks.Http $SINKS_HTTP, Serilog.Extensions.Logging $EXTENSIONS_LOGGING and Newtonsoft.Json $NEWTONSOFT"

# Selects SDK $1 for every dotnet command below the repository root, and nothing else: both SDKs are installed.
use_sdk() { printf '{"sdk": {"version": "%s", "rollForward": "disable"}}\n' "$1" > global.json; }

# The first error MSBuild or NuGet reports in log $2, after the reason $1.
fail() {
  echo "::error::$1: $(grep -m 1 -E ': error |^error|Unhandled exception' "$2" | sed 's/^ *//' || tail -n 1 "$2")"
  exit 1
}

# The version of package $1 the example resolved, from project.assets.json. A .NET Framework exe also gets a
# runtime-specific target (net48/win-x86) with the same packages, only the plain one is read.
resolved() {
  jq -r --arg id "$1" '.targets | to_entries | map(select(.key | contains("/") | not)) | .[0].value | keys[]
    | select(ascii_downcase | startswith(($id | ascii_downcase) + "/")) | sub("^[^/]*/"; "")' "$ASSETS"
}

case "$1" in
  pack)
    use_sdk "$PACK_SDK"
    # The project packs on build (GeneratePackageOnBuild), which `dotnet pack` cannot be combined with. A relative
    # output path, unlike release.yml's $PWD/artifacts: Git Bash would hand MSBuild a /d/a/... path on Windows.
    dotnet build --configuration Release -p:Version="$CLIENT" -p:ContinuousIntegrationBuild=true -p:PackageOutputPath=artifacts 2>&1 | tee out/pack.log \
      || fail "Packing the client failed" out/pack.log
    [ -f "artifacts/$ID.$CLIENT.nupkg" ] || { echo "::error::Packing left no artifacts/$ID.$CLIENT.nupkg"; exit 1; }
    ;;

  install)
    use_sdk "$SDK"
    # The example targets net10.0. A customer on .NET Framework also needs a newer C# than its default 7.3 for the
    # top-level statements.
    FRAMEWORK="<TargetFramework>$TFM</TargetFramework>"
    if [[ "$TFM" == net4* ]]; then FRAMEWORK+="<LangVersion>latest</LangVersion>"; fi
    sed "s|<TargetFramework>[^<]*</TargetFramework>|$FRAMEWORK|" example-project/ExampleProject.csproj > out/ExampleProject.csproj
    mv out/ExampleProject.csproj example-project/ExampleProject.csproj
    grep -q "<TargetFramework>$TFM</TargetFramework>" example-project/ExampleProject.csproj \
      || { echo "::error::example-project/ExampleProject.csproj has no <TargetFramework> to replace"; exit 1; }
    # Pinned, so that NuGet cannot resolve another version without failing: its nearest-wins rule would otherwise
    # take the oldest version that the client's own dependencies accept.
    for package in "Serilog $SERILOG" "Serilog.Sinks.Http $SINKS_HTTP" "Serilog.Extensions.Logging $EXTENSIONS_LOGGING" \
      "Newtonsoft.Json $NEWTONSOFT" "Serilog.Sinks.Console $CONSOLE"; do
      read -r name version <<<"$package"
      dotnet add example-project package "$name" --version "$version" --no-restore > /dev/null
    done
    SOURCES=(--source "$NUGET_ORG")
    if [ "$CLIENT_FROM" = checkout ]; then
      # The packed version exists only in artifacts/, so the example cannot take a release from nuget.org instead.
      dotnet add example-project package "$ID" --version "$CLIENT" --no-restore > /dev/null
      SOURCES+=(--source "$PWD/artifacts")
    fi
    # On the schedule the example keeps its own reference to the client, 1.*, which has to take the newest release.
    cat example-project/ExampleProject.csproj
    # Absolute paths: NuGet resolves relative ones against the project's directory.
    dotnet restore example-project --packages "$PWD/$PACKAGES" "${SOURCES[@]}" 2>&1 | tee out/restore.log \
      || fail "Restoring $WHAT failed" out/restore.log
    ;;

  versions)
    # dotnet ends its lines with CRLF on Windows.
    SDK_RUNNING=$(dotnet --version | tr -d '\r')
    if [[ "$TFM" == net4* ]]; then
      # Windows PowerShell runs on the .NET Framework, the only one installed, which runs the example too.
      RUNTIME_RUNNING=$(powershell -NoProfile -Command '[System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription' | tr -d '\r')
    else
      # A framework-dependent app runs on the newest patch installed of the runtime it targets.
      RUNTIME_RUNNING=$(dotnet --list-runtimes | tr -d '\r' | sed -n "s/^Microsoft\.NETCore\.App \(${TFM#net}\.[^ ]*\) .*/\1/p" | sort -V | tail -n 1)
      RUNTIME_RUNNING=${RUNTIME_RUNNING:+Microsoft.NETCore.App $RUNTIME_RUNNING}
    fi
    LIBRARY=$(jq -r --arg id "$ID/" '.libraries | keys[] | select(startswith($id))' "$ASSETS")
    CLIENT_RESOLVED=${LIBRARY#*/}
    # NuGet records in each package it extracts the source it came from.
    CLIENT_SOURCE=$(jq -r '.source' "$PACKAGES/$(jq -r --arg library "$LIBRARY" '.libraries[$library].path' "$ASSETS")/.nupkg.metadata")
    if [ "$CLIENT_FROM" = checkout ]; then FROM="packed from the checkout"; else FROM="from nuget.org"; fi
    INSTALLED="$TFM on $RUNTIME_RUNNING, .NET SDK $SDK_RUNNING, $ID $CLIENT_RESOLVED $FROM, Serilog $(resolved Serilog), Serilog.Sinks.Http $(resolved Serilog.Sinks.Http), Serilog.Extensions.Logging $(resolved Serilog.Extensions.Logging), Newtonsoft.Json $(resolved Newtonsoft.Json), Serilog.Sinks.Console $(resolved Serilog.Sinks.Console)"
    echo "- $INSTALLED" | tee -a out/versions.md "${GITHUB_STEP_SUMMARY:-/dev/null}"
    # The errors of the later steps name the versions: the Linear issue shows them when no read-back summary exists.
    echo "INSTALLED=$INSTALLED" >> "${GITHUB_ENV:-/dev/null}"

    FAILED=0
    if [ "$SDK_RUNNING" != "$SDK" ]; then
      echo "::error::.NET SDK $SDK_RUNNING is running, but the $LEG leg plans $SDK."; FAILED=1
    fi
    # The planned runtime is exact (10.0.14), or the .NET Framework's minor (4.8), which Windows updates in place.
    case "$RUNTIME_RUNNING" in
      "Microsoft.NETCore.App $RUNTIME" | "$RUNTIME" | "$RUNTIME".*) ;;
      *) echo "::error::The example runs on ${RUNTIME_RUNNING:-no runtime for $TFM}, but the $LEG leg plans $RUNTIME."; FAILED=1 ;;
    esac
    if [ "$CLIENT_RESOLVED" != "$CLIENT" ]; then
      if [ "$CLIENT_FROM" = nuget ]; then
        echo "::error::The example resolved $ID ${CLIENT_RESOLVED:-to nothing}, but the newest release on nuget.org is $CLIENT: update the version range in example-project/ExampleProject.csproj."
      else
        echo "::error::The example resolved $ID ${CLIENT_RESOLVED:-to nothing}, but the $LEG leg packed $CLIENT."
      fi
      FAILED=1
    fi
    # artifacts/ for the packed client, nuget.org for a release. The path is NuGet's, with backslashes on Windows.
    if [ "$CLIENT_FROM" = checkout ]; then
      [[ "$CLIENT_SOURCE" =~ [/\\]artifacts[/\\]?$ ]] || { echo "::error::NuGet took $ID $CLIENT_RESOLVED from $CLIENT_SOURCE, not from artifacts/."; FAILED=1; }
    elif [ "$CLIENT_SOURCE" != "$NUGET_ORG" ]; then
      echo "::error::NuGet took $ID $CLIENT_RESOLVED from $CLIENT_SOURCE, not from nuget.org."; FAILED=1
    fi
    # The canary's pre-releases only report: a pre-release can depend on a newer pre-release of another package.
    if [ "$LEG" != canary ]; then
      for check in "Serilog $SERILOG" "Serilog.Sinks.Http $SINKS_HTTP" "Serilog.Extensions.Logging $EXTENSIONS_LOGGING" "Newtonsoft.Json $NEWTONSOFT"; do
        read -r name want <<<"$check"
        got=$(resolved "$name")
        if [ "$got" != "$want" ]; then
          echo "::error::The example resolved $name ${got:-to nothing}, but the $LEG leg plans $want."; FAILED=1
        fi
      done
    fi
    exit "$FAILED"
    ;;

  build)
    dotnet build example-project --no-restore 2>&1 | tee out/build.log || fail "Building $WHAT failed" out/build.log
    if [ "$CLIENT_FROM" = checkout ]; then
      # The client the example runs with is, byte for byte, the one packed above: the build of the asset NuGet
      # picked for this framework, e.g. lib/netstandard2.0/ for .NET Framework.
      LIBRARY=$(jq -r --arg id "$ID/" '.libraries | keys[] | select(startswith($id))' "$ASSETS")
      ASSET=$(jq -r --arg library "$LIBRARY" '.targets | to_entries | map(select(.key | contains("/") | not)) | .[0].value[$library].runtime | keys[0]' "$ASSETS")
      PACKED="bin/Release/$(basename "$(dirname "$ASSET")")/$ID.dll"
      COPIED=$(find example-project/bin -name "$ID.dll" | head -n 1)
      if ! cmp "$PACKED" "$COPIED"; then
        echo "::error::$COPIED, which the example runs with, is not $PACKED, the client packed from the checkout."; exit 1
      fi
      echo "$COPIED is $PACKED, the client packed from the checkout ($ASSET)"
    fi
    ;;

  run)
    # The example closes the logger and exits 1 when Serilog reported a problem, such as a batch Better Stack rejected.
    dotnet run --project example-project --no-build 2>&1 | tee out/example.log \
      || { echo "::error::The example failed with ${INSTALLED:-$WHAT}: $(tail -n 1 out/example.log)"; exit 1; }
    ;;

  *) echo "::error::unknown command $1"; exit 1 ;;
esac
