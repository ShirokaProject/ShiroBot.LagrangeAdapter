#!/usr/bin/env sh
set -eu

prepare_only=false
skip_build=false
if [ "${1:-}" = "--prepare" ]; then prepare_only=true; shift; fi
if [ "${1:-}" = "--no-build" ]; then skip_build=true; shift; fi

cd "$(dirname "$0")/.."

sdk_version=$(sed -n 's/.*PackageReference Include="ShiroBot.SDK" Version="\([^"]*\)".*/\1/p' src/ShiroBot.LagrangeAdapter.csproj)
if [ -z "$sdk_version" ]; then
  echo 'Cannot read ShiroBot.SDK version from src/ShiroBot.LagrangeAdapter.csproj.' >&2
  exit 1
fi

case "$(uname -s)" in
  Darwin) platform=osx ;;
  Linux)
    platform=linux
    if [ -f /etc/alpine-release ]; then platform=linux-musl; fi
    ;;
  *) echo 'Unsupported operating system. On Windows, use dev.ps1.' >&2; exit 1 ;;
esac

case "$(uname -m)" in
  x86_64|amd64) architecture=x64 ;;
  arm64|aarch64) architecture=arm64 ;;
  *) echo 'Unsupported CPU architecture.' >&2; exit 1 ;;
esac

rid="$platform-$architecture"
# The host directory is not versioned: config.toml, plugins/ and adapters/ survive SDK upgrades.
cache_dir=".shirobot-dev/host/$rid"
host_exe="$cache_dir/ShiroBot"
version_file="$cache_dir/.host-version"
archive="$cache_dir/shirobot-host-$rid-framework-dependent.zip"

if [ "$skip_build" = false ]; then
  dotnet build src/ShiroBot.LagrangeAdapter.csproj -c Debug -p:ShiroBotPluginPackagingEnabled=false -p:ShiroBotPrepareDevelopmentHost=false
fi

# An optional local build supplies unreleased host/Dashboard support without copying user data.
source_file=".shirobot-dev/host-source"
if [ -n "${SHIROBOT_DEV_HOST_SOURCE:-}" ]; then
  mkdir -p .shirobot-dev
  printf '%s\n' "$SHIROBOT_DEV_HOST_SOURCE" > "$source_file"
fi
local_source=$(cat "$source_file" 2>/dev/null || true)
if [ -n "$local_source" ]; then
  [ -f "$local_source/ShiroBot" ] || { echo "Local host executable missing: $local_source/ShiroBot" >&2; exit 1; }
  mkdir -p "$cache_dir"
  copy_host_file() {
    source_file=$1
    target_file=$2
    if [ -f "$target_file" ] && cmp -s "$source_file" "$target_file"; then return; fi
    mkdir -p "$(dirname "$target_file")"
    cp "$source_file" "$target_file.dev-copy"
    mv -f "$target_file.dev-copy" "$target_file"
  }
  for file in "$local_source"/*; do
    case "$file" in
      *.dll|*.pdb|*.deps.json|*.runtimeconfig.json|*/ShiroBot) copy_host_file "$file" "$cache_dir/$(basename "$file")" ;;
    esac
  done
  for directory in runtimes Assets; do
    if [ -d "$local_source/$directory" ]; then
      find "$local_source/$directory" -type f | while IFS= read -r file; do
        relative=${file#"$local_source/"}
        copy_host_file "$file" "$cache_dir/$relative"
      done
    fi
  done
  chmod +x "$host_exe"
  printf '%s\n' "$sdk_version" > "$version_file"
fi

installed_version=$(cat "$version_file" 2>/dev/null || true)
if [ ! -f "$host_exe" ] || [ "$installed_version" != "$sdk_version" ]; then
  mkdir -p "$cache_dir"
  url="https://github.com/ShirokaProject/ShiroBot/releases/download/v$sdk_version/$(basename "$archive")"
  echo "Downloading ShiroBot v$sdk_version for $rid..."
  curl --fail --location --retry 2 "$url" --output "$archive"
  # The archive only contains the host executable, so the existing local data is kept.
  unzip -q -o "$archive" -d "$cache_dir"
  rm -f "$archive"
  chmod +x "$host_exe"
  printf '%s\n' "$sdk_version" > "$version_file"
fi

adapter_dir="$cache_dir/adapters/lagrange"
build_dir="src/bin/Debug/net11.0"
manifest="$adapter_dir/.shirobot-dev-files"
mkdir -p "$adapter_dir"
# Remove files copied by the previous run that the build no longer produces. Files the
# adapter or host created there (config.toml, data, .shirobot/native, ...) are never touched.
if [ -f "$manifest" ]; then
  while IFS= read -r file; do
    case "$file" in ""|*..*) continue ;; esac
    [ -e "$build_dir/$file" ] || rm -f "$adapter_dir/$file"
  done < "$manifest"
fi
cp -R "$build_dir/." "$adapter_dir/"
# config.toml is never listed, so a user-edited config is not deleted if the build stops emitting one.
(cd "$build_dir" && find . -type f ! -name config.toml | sed 's#^\./##') > "$manifest"
# The host discovers the adapter entry from DLL metadata; no package-local manifest is needed.

if [ ! -f "$cache_dir/config.toml" ]; then
  printf 'protocols = ["lagrange"]\n\n[api]\nenable = true\nlisten_urls = ["http://127.0.0.1:7002"]\n' > "$cache_dir/config.toml"
fi

# Create one enabled development instance; authentication remains manual by default.
if [ ! -f "$adapter_dir/config.toml" ]; then
  printf '[[instances]]\nid = "lagrange-dev"\nname = "Lagrange Dev"\nenabled = true\n\n[instances.config]\nauto_login = false\n' > "$adapter_dir/config.toml"
fi
# Apphosts must use the same SDK/runtime root as dotnet, rather than another global install.
if [ -z "${DOTNET_ROOT:-}" ]; then
  dotnet_exe=$(command -v dotnet)
  if command -v realpath >/dev/null 2>&1; then dotnet_exe=$(realpath "$dotnet_exe"); fi
  DOTNET_ROOT=$(dirname "$dotnet_exe")
  export DOTNET_ROOT
fi

if [ "$prepare_only" = true ]; then
  echo "ShiroBot v$sdk_version is ready. Use the Rider Run button to start it."
  exit 0
fi

echo "Starting ShiroBot v$sdk_version with Lagrange V2..."
cd "$cache_dir"
exec ./ShiroBot "$@"
