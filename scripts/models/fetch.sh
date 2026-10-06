#!/usr/bin/env bash
# Fetches the subject detection models that are not kept in git, checks each against the SHA-256 and
# size recorded here and in src/Composa.Core/Vision/SubjectModels.cs, and puts them where the build
# copies them from (src/Composa.Core/Models/). A file that already matches is left alone, so this is
# cheap to run before every build; a download that does not match is deleted and the script fails.
#
#   usage: scripts/models/fetch.sh
#
# Nothing here runs at application run time: Composa never downloads a model. The packaging scripts
# and the workflows run this so every package carries every model; a developer checkout that has not
# run it works without the Person choice, which falls back to the plain backdrop and says so.
#
# Both pins are the community ONNX conversions Lolly uses, hashed on 2026-10-01 against Lolly's pins:
#   u2netp.onnx  is in git (4.6 MB), from rembg's release page; upstream xuebinqin/U-2-Net, Apache-2.0.
#   modnet.onnx  26 MB, from Xenova/modnet on Hugging Face; upstream ZHKKKe/MODNet, Apache-2.0.
# This is a new subject-only packaging script in this fork (no Models or scripts/models
# files existed at HEAD). Real-ESRGAN is not bundled. The two promptable SAM encoder/decoder
# pairs are committed and verified below; provenance and licenses travel with the application.
# A new model goes here, into SubjectModels.cs and into packaging/THIRD-PARTY-NOTICES.txt in one change,
# and only with weights under a permissive licence (Apache-2.0, MIT or BSD): BRIA's RMBG models are
# non-commercial and never ship.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIR="$ROOT/src/Composa.Core/Models"

fetch() {
  local file="$1" url="$2" sha="$3" bytes="$4" path="$DIR/$1"
  if [ -f "$path" ] && [ "$(sha256sum "$path" | cut -d' ' -f1)" = "$sha" ]; then
    echo "fetch.sh: $file is present and verified"
    return 0
  fi
  echo "fetch.sh: fetching $file ($((bytes / 1024 / 1024)) MB) from $url"
  mkdir -p "$DIR"
  curl -fsSL --retry 3 -o "$path.part" "$url"
  local actual
  actual="$(sha256sum "$path.part" | cut -d' ' -f1)"
  if [ "$actual" != "$sha" ] || [ "$(stat -c %s "$path.part" 2>/dev/null || stat -f %z "$path.part")" != "$bytes" ]; then
    rm -f "$path.part"
    echo "fetch.sh: $file did not match its pin (SHA-256 $actual, expected $sha); nothing was kept." >&2
    exit 1
  fi
  mv "$path.part" "$path"
  echo "fetch.sh: $file verified"
}

fetch modnet.onnx \
  "https://huggingface.co/Xenova/modnet/resolve/main/onnx/model.onnx" \
  07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9 25888640

# These are committed; this only confirms the checkout still carries the right files.
check() {
  if [ "$(sha256sum "$DIR/$1" | cut -d' ' -f1)" != "$2" ]; then
    echo "fetch.sh: src/Composa.Core/Models/$1 is not the file the catalog names." >&2
    exit 1
  fi
  echo "fetch.sh: $1 is present and verified"
}
check u2netp.onnx 309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8
check mobile_sam_encoder.onnx 20deef402855b31222b528f52b04807e41ebe47216ac0e39a0729f43491a0209
check mobile_sam_decoder.onnx 22cf85e35d14182f4b4712364264c06b22edbef63f065189586f080ef4e2f325
check efficient_sam_vitt_encoder.onnx 7a73ee65aa2c37237c89b4b18e73082f757ffb173899609c5d97a2bbd4ebb02d
check efficient_sam_vitt_decoder.onnx e1afe46232c3bfa3470a6a81c7d3181836a94ea89528aff4e0f2d2c611989efd
