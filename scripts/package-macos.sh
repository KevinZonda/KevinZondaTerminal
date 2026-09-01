#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
project="$repo_root/src/KevinZonda.Terminal.AvaloniaDesktop/KevinZonda.Terminal.AvaloniaDesktop.csproj"
icon_source="$repo_root/assets/WinFormsDefault.ico"
plist_template="$repo_root/packaging/macos/Info.plist.in"
entitlements="$repo_root/packaging/macos/entitlements.plist"

app_name="${MACOS_APP_NAME:-KevinZonda Terminal}"
bundle_id="${MACOS_BUNDLE_ID:-com.kevinzonda.terminal}"
executable_name="kterm"
configuration="${CONFIGURATION:-Release}"
sign_identity="${MACOS_SIGN_IDENTITY:--}"
publish_trimmed="${MACOS_PUBLISH_TRIMMED:-true}"

if [[ -z "$app_name" || "$app_name" == */* || "$app_name" == "." || "$app_name" == ".." ]]; then
  echo "MACOS_APP_NAME must be a non-empty file name without path separators." >&2
  exit 1
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS app bundles must be packaged on macOS." >&2
  exit 1
fi

if [[ "$publish_trimmed" != "true" && "$publish_trimmed" != "false" ]]; then
  echo "MACOS_PUBLISH_TRIMMED must be true or false." >&2
  exit 1
fi

case "$(uname -m)" in
  arm64) host_rid="osx-arm64" ;;
  x86_64) host_rid="osx-x64" ;;
  *)
    echo "Unsupported macOS architecture: $(uname -m)" >&2
    exit 1
    ;;
esac

rid="${1:-$host_rid}"
if [[ "$rid" != "$host_rid" ]]; then
  echo "Cross-architecture packaging is not supported because the PTY helper is compiled for the host architecture ($host_rid)." >&2
  exit 1
fi

for command_name in dotnet sips plutil codesign ditto file xattr; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command not found: $command_name" >&2
    exit 1
  fi
done

version="$(dotnet msbuild "$project" -nologo -getProperty:Version)"
bundle_version="${version%%-*}"
if [[ ! "$bundle_version" =~ ^[0-9]+([.][0-9]+){0,2}$ ]]; then
  echo "Project version '$version' is not valid for CFBundleVersion." >&2
  exit 1
fi

publish_dir="$repo_root/artifacts/publish/$rid"
artifact_dir="$repo_root/artifacts/macos/$rid"
app_bundle="$artifact_dir/$app_name.app"
contents_dir="$app_bundle/Contents"
macos_dir="$contents_dir/MacOS"
resources_dir="$contents_dir/Resources"

case "$app_bundle" in
  "$repo_root"/artifacts/macos/*.app) ;;
  *)
    echo "Refusing to replace an app bundle outside the expected artifacts directory: $app_bundle" >&2
    exit 1
    ;;
esac

echo "Publishing $rid self-contained app..."
if [[ -e "$publish_dir" ]]; then
  rm -rf -- "$publish_dir"
fi
publish_args=(
  -c "$configuration"
  -r "$rid"
  --self-contained true
  --nologo
  -o "$publish_dir"
)
if [[ "$publish_trimmed" == "true" ]]; then
  publish_args+=(-p:PublishTrimmed=true -p:TrimMode=partial)
fi
dotnet publish "$project" \
  "${publish_args[@]}"

if [[ ! -x "$publish_dir/$executable_name" ]]; then
  echo "Published executable not found: $publish_dir/$executable_name" >&2
  exit 1
fi

if [[ -e "$app_bundle" ]]; then
  rm -rf -- "$app_bundle"
fi
mkdir -p "$macos_dir" "$resources_dir"
ditto "$publish_dir" "$macos_dir"
# Build inputs can inherit Finder quarantine or resource-fork metadata. Clear
# removable extended attributes before creating the code signature.
xattr -cr "$app_bundle"
# NuGet packages can carry executable mode bits on managed PE assemblies. macOS
# treats executable files as nested code during strict bundle verification, so
# normalize permissions and restore them only for native executables below.
find "$macos_dir" -type f -exec chmod a-x {} +
chmod +x "$macos_dir/$executable_name"

cp "$plist_template" "$contents_dir/Info.plist"
plutil -replace CFBundleDisplayName -string "$app_name" "$contents_dir/Info.plist"
plutil -replace CFBundleName -string "$app_name" "$contents_dir/Info.plist"
plutil -replace CFBundleIdentifier -string "$bundle_id" "$contents_dir/Info.plist"
plutil -replace CFBundleShortVersionString -string "$bundle_version" "$contents_dir/Info.plist"
plutil -replace CFBundleVersion -string "$bundle_version" "$contents_dir/Info.plist"
plutil -lint "$contents_dir/Info.plist" >/dev/null

sips -s format icns "$icon_source" \
  --out "$resources_dir/KevinZondaTerminal.icns" >/dev/null

sign_args=(--force --sign "$sign_identity")
if [[ "$sign_identity" != "-" ]]; then
  sign_args+=(--options runtime --timestamp)
fi

while IFS= read -r -d '' candidate; do
  if [[ "$candidate" == "$macos_dir/$executable_name" ]]; then
    continue
  fi
  file_description="$(file -b "$candidate")"
  if [[ "$file_description" == *Mach-O* || "$file_description" == PE32* ]]; then
    if [[ "$file_description" == *executable* ]]; then
      chmod +x "$candidate"
    fi
    codesign "${sign_args[@]}" "$candidate"
  fi
done < <(find "$macos_dir" -type f -print0)

# The self-contained .NET payload lives directly under Contents/MacOS. --deep
# makes codesign seal the managed PE assemblies and other nested payload files
# while the explicit pass above still signs native binaries inside-out.
codesign --deep "${sign_args[@]}" --entitlements "$entitlements" "$app_bundle"
codesign --verify --deep --strict --verbose=1 "$app_bundle"

echo
echo "Created: $app_bundle"
if [[ "$sign_identity" == "-" ]]; then
  echo "Signing: ad hoc (local testing only)"
else
  echo "Signing: $sign_identity"
fi
echo "Trimmed: $publish_trimmed"
