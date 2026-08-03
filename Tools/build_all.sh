#!/bin/bash
# Build both headset APKs from THIS single source tree, injecting each app's
# identity at build time so ProjectSettings.asset is never edited in the working
# tree. See Assets/Editor/BuildAndroid.cs.
#
#   UNITY=/path/to/Unity ./Tools/build_all.sh
#
# Requires Unity 6000.2.12f1 (this project's editor version) with Android Build
# Support. Set UNITY to the Unity executable, or export it in your shell.
set -euo pipefail

PROJECT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:?set UNITY to the Unity 6000.2.12f1 executable, e.g. UNITY=~/Unity/Hub/Editor/6000.2.12f1/Editor/Unity}"
OUT="$PROJECT/Build"

build() {
  local out="$1" product="$2" appid="$3"
  echo ">>> building $product ($appid) -> $out"
  "$UNITY" -quit -batchmode -nographics \
    -projectPath "$PROJECT" \
    -buildTarget Android \
    -executeMethod BuildAndroid.Build \
    -output "$out" \
    -productName "$product" \
    -applicationIdentifier "$appid" \
    -logFile -
}

# Default identity (Meta_hand) matches committed ProjectSettings, so passing it
# explicitly is redundant but keeps both invocations symmetric and self-documenting.
build "$OUT/Meta_hand.apk"       Meta_hand       com.dongridong.metahand
build "$OUT/Meta_controller.apk" Meta_controller com.dongridong.metacontroller

echo ">>> done. APKs in $OUT"
