#!/bin/bash
# Downloads one YMM4 Lite version the way YMM4's own updater does (YukkuriMovieMaker.Update.Updater):
#   versionlist2.php                                   -> the published versions, one per line
#   Application Files/YukkuriMovieMaker_A_B_C_D/YukkuriMovieMaker.json -> {Files: [{File, Hash, Size}], HashAlgorithm}
#   Application Files/YukkuriMovieMaker_A_B_C_D/<File> -> each file, checked against Hash (base64) and Size
# Usage: fetch-ymm4.sh list
#        fetch-ymm4.sh <version|latest> <destination> [--dlls]   (--dlls: only the top-level YukkuriMovieMaker*.dll)
# Prints the resolved version on the last line. For CI only; the binaries are never committed.
set -euo pipefail
BASE='https://manjubox.net/Install/YukkuriMovieMaker_v4_Lite'
curl_() { curl -fsSL --retry 4 --retry-delay 5 --connect-timeout 30 --max-time 600 "$@"; }

versions() { curl_ "$BASE/versionlist2.php" | tr -d '\r' | grep -E '^[0-9]+(\.[0-9]+){3}$' | sort -t. -k1,1nr -k2,2nr -k3,3nr -k4,4nr; }

if [ "${1:-}" = list ]; then versions; exit 0; fi
version=${1:?version or latest}
dest=${2:?destination directory}
filter='.'
[ "${3:-}" = --dlls ] && filter='^YukkuriMovieMaker[^\\\\]*\.dll$'
[ "$version" = latest ] && version=$(versions | head -1)
[[ "$version" =~ ^[0-9]+(\.[0-9]+){3}$ ]] || { echo "bad version: $version" >&2; exit 1; }
folder="$BASE/Application%20Files/YukkuriMovieMaker_${version//./_}"
mkdir -p "$dest"
manifest=$(mktemp)
curl_ "$folder/YukkuriMovieMaker.json" | sed '1s/^\xEF\xBB\xBF//' > "$manifest"
algorithm=$(jq -r '.HashAlgorithm // "SHA256"' "$manifest" | tr '[:upper:]' '[:lower:]')
count=0
while IFS=$'\t' read -r file hash size; do
  path="$dest/${file//\\//}"
  mkdir -p "$(dirname "$path")"
  url="$folder/$(printf '%s' "${file//\\//}" | jq -sRr '@uri' | sed 's/%2F/\//g')"
  if [ ! -f "$path" ] || [ "$(stat -c %s "$path")" != "$size" ] \
     || [ "$(openssl dgst -"$algorithm" -binary "$path" | base64 -w0)" != "$hash" ]; then
    curl_ -o "$path" "$url"
  fi
  [ "$(stat -c %s "$path")" = "$size" ] || { echo "size mismatch: $file" >&2; exit 1; }
  [ "$(openssl dgst -"$algorithm" -binary "$path" | base64 -w0)" = "$hash" ] || { echo "hash mismatch: $file" >&2; exit 1; }
  count=$((count + 1))
done < <(jq -r --arg f "$filter" '.Files[] | select(.File | test($f)) | [.File, .Hash, (.Size|tostring)] | @tsv' "$manifest")
rm -f "$manifest"
echo "fetched $count files of YMM4 $version into $dest" >&2
echo "$version"
