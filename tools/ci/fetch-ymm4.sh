#!/bin/bash
# Downloads one YMM4 Lite version the way YMM4's own updater does (YukkuriMovieMaker.Update.Updater):
#   versionlist2.php                                   -> the published versions, one per line
#   Application Files/YukkuriMovieMaker_A_B_C_D/YukkuriMovieMaker.json -> {Files: [{File, Hash, Size}], HashAlgorithm}
#   Application Files/YukkuriMovieMaker_A_B_C_D/<File> -> each file, checked against Hash (base64) and Size
# Usage: fetch-ymm4.sh list
#        fetch-ymm4.sh manifest <version>  (the file list with hashes and sizes, as JSON)
#        fetch-ymm4.sh <version|latest> <destination> [--dlls|--top|--scan|--match <regex>]
#          --dlls: only the top-level YukkuriMovieMaker*.dll; --top: the files of the application folder itself
#          (not Resources and other subfolders, which hold voice data and dictionaries); --scan: the top-level
#          YukkuriMovieMaker*.dll and the runtime configuration (tools/compat); --match: files whose path matches
# Prints the resolved version on the last line. For CI only; the binaries are never committed.
set -euo pipefail
BASE=${YMM4_UPDATE_BASE:-https://manjubox.net/Install/YukkuriMovieMaker_v4_Lite}
curl_() { curl -fsSL --retry 4 --retry-delay 5 --connect-timeout 30 --max-time 600 "$@"; }

versions() { curl_ "$BASE/versionlist2.php" | tr -d '\r' | grep -E '^[0-9]+(\.[0-9]+){3}$' | sort -t. -k1,1nr -k2,2nr -k3,3nr -k4,4nr; }

if [ "${1:-}" = list ]; then versions; exit 0; fi
manifest_() { curl_ "$BASE/Application%20Files/YukkuriMovieMaker_${1//./_}/YukkuriMovieMaker.json" | sed '1s/^\xEF\xBB\xBF//'; }
if [ "${1:-}" = manifest ]; then manifest_ "${2:?version}"; exit 0; fi
version=${1:?version or latest}
dest=${2:?destination directory}
filter='.'
[ "${3:-}" = --dlls ] && filter='^YukkuriMovieMaker[^\\\\]*\.dll$'
[ "${3:-}" = --top ] && filter='^[^\\\\]+$'
[ "${3:-}" = --scan ] && filter='^((YukkuriMovieMaker|Vortice\.|SharpGen\.)[^\\\\]*\.dll|Newtonsoft\.Json\.dll|YukkuriMovieMaker\.runtimeconfig\.json)$'
[ "${3:-}" = --match ] && filter=${4:?regex}
[ "$version" = latest ] && version=$(versions | head -1)
[[ "$version" =~ ^[0-9]+(\.[0-9]+){3}$ ]] || { echo "bad version: $version" >&2; exit 1; }
folder="$BASE/Application%20Files/YukkuriMovieMaker_${version//./_}"
mkdir -p "$dest"
manifest=$(mktemp)
manifest_ "$version" > "$manifest"
# tr -d '\r': jq on Windows (Git Bash on a CI runner) ends its lines with CRLF.
algorithm=$(jq -r '.HashAlgorithm // "SHA256"' "$manifest" | tr -d '\r' | tr '[:upper:]' '[:lower:]')
# Two downloads at a time, as YMM4's updater does.
fetch_one() {
  local file=$1 hash=$2 size=$3 path url
  path="$dest/${file//\\//}"
  mkdir -p "$(dirname "$path")"
  url="$folder/$(printf '%s' "${file//\\//}" | jq -sRr '@uri' | sed 's/%2F/\//g')"
  if [ ! -f "$path" ] || [ "$(stat -c %s "$path")" != "$size" ] \
     || [ "$(openssl dgst -"$algorithm" -binary "$path" | base64 -w0)" != "$hash" ]; then
    curl_ -o "$path" "$url"
  fi
  [ "$(stat -c %s "$path")" = "$size" ] || { echo "size mismatch: $file" >&2; return 1; }
  [ "$(openssl dgst -"$algorithm" -binary "$path" | base64 -w0)" = "$hash" ] || { echo "hash mismatch: $file" >&2; return 1; }
}
export -f fetch_one curl_
export dest folder algorithm
jq -r --arg f "$filter" '.Files[] | select(.File | test($f)) | [.File, .Hash, (.Size|tostring)] | @tsv' "$manifest" | tr -d '\r' > "$manifest.list"
count=$(wc -l < "$manifest.list")
tr '\t' '\n' < "$manifest.list" | xargs -d '\n' -n 3 -P 2 bash -c 'fetch_one "$@"' _
rm -f "$manifest.list"
rm -f "$manifest"
echo "fetched $count files of YMM4 $version into $dest" >&2
echo "$version"
