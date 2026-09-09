#!/usr/bin/env bash

# Publishes the built mod folder to the workshop item named in base.vdf.
#
#   dotnet build -t:SteamPublish          from the project folder, the normal way
#   bash Steam/SteamPublish.sh <folder>   by hand, folder being what to upload
#
# With no folder it takes the installed copy under SPZ2_PERSISTENT, and refuses to run if
# that does not look like a built mod. It used to accept an empty content path, which
# steamcmd is perfectly happy with: it uploads the preview image, reports "Success", and
# leaves the item's files exactly as they were.

set -u

CONTENT_PATH=${1:-}

# Everything is resolved from where this script lives, not from the working directory.
# Composing paths out of $PWD meant running it from inside Steam/ produced Steam\Steam\.
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
BASE_VDF="$SCRIPT_DIR/base.vdf"
TMP_VDF_POSIX="$SCRIPT_DIR/base.tmp.vdf"

# --- what to upload ---------------------------------------------------------

if [ -z "$CONTENT_PATH" ]; then
  INSTALLED="${SPZ2_PERSISTENT:-}/mods/PlatformEfficiencyOverlay"

  if [ -n "${SPZ2_PERSISTENT:-}" ] && [ -d "$INSTALLED" ]; then
    CONTENT_PATH="$INSTALLED"
    echo "no content folder given; using the installed mod: $CONTENT_PATH"
  else
    echo "error: no content folder given and none found under SPZ2_PERSISTENT." >&2
    echo "       Pass the folder to upload, or publish with:" >&2
    echo "           dotnet build -t:SteamPublish" >&2
    exit 1
  fi
fi

# Refuse to publish something that is not a built mod. An empty or wrong folder is the one
# mistake here that looks like success.
for required in manifest.json PlatformEfficiencyOverlay.dll; do
  if [ ! -f "$CONTENT_PATH/$required" ]; then
    echo "error: $CONTENT_PATH has no $required, so it is not a built mod folder." >&2
    echo "       Build first, then publish." >&2
    exit 1
  fi
done

VERSION=$(grep -m1 '"Version"' "$CONTENT_PATH/manifest.json" | sed 's/.*"Version"[^"]*"\([^"]*\)".*/\1/')
echo "publishing version ${VERSION:-unknown} from $CONTENT_PATH"

# --- the paths steamcmd wants, which are Windows ones with escaped separators -----------

CONTENT_PATH=$(cygpath -w "$CONTENT_PATH")
PREVIEW_IMG=$(cygpath -w "$SCRIPT_DIR/preview.png")

CONTENT_PATH="${CONTENT_PATH//\\/\\\\}"
PREVIEW_IMG="${PREVIEW_IMG//\\/\\\\}"

echo "CONTENT_PATH: $CONTENT_PATH"
echo "PREVIEW_IMG: $PREVIEW_IMG"

export CONTENT_PATH
export PREVIEW_IMG

# Fill the absolute paths into a copy, leaving base.vdf as the checked-in template.
envsubst < "$BASE_VDF" > "$TMP_VDF_POSIX"

cat "$TMP_VDF_POSIX"

TMP_VDF=$(cygpath -w "$TMP_VDF_POSIX")

# --- steamcmd ---------------------------------------------------------------

# It is usually not on PATH, and it has to live somewhere writable because it self-updates
# into its own folder - which rules out Program Files.
find_steamcmd() {
  if [ -n "${STEAMCMD:-}" ] && [ -x "$STEAMCMD" ]; then
    printf '%s' "$STEAMCMD"
    return 0
  fi

  if command -v steamcmd >/dev/null 2>&1; then
    command -v steamcmd
    return 0
  fi

  for candidate in "$HOME/steamcmd/steamcmd.exe" "C:/steamcmd/steamcmd.exe" "${PROGRAMFILES:-C:/Program Files}/SteamCMD/steamcmd.exe" "${LOCALAPPDATA:-}/SteamCMD/steamcmd.exe"
  do
    if [ -x "$candidate" ]; then
      printf '%s' "$candidate"
      return 0
    fi
  done

  return 1
}

STEAMCMD_BIN=$(find_steamcmd || true)

if [ -z "$STEAMCMD_BIN" ]; then
  echo "error: steamcmd not found. Install it somewhere writable (not Program Files -" >&2
  echo "       it self-updates into its own folder) and either put it on PATH or set" >&2
  echo "       STEAMCMD to the full path of steamcmd.exe." >&2
  exit 1
fi

echo "STEAMCMD: $STEAMCMD_BIN"

# Which account to publish as. steamcmd remembers the accounts it has logged in with, so
# after the first interactive login there is nothing to set: the name comes out of its own
# config. STEAM_LOGIN overrides it, and is only needed when more than one is cached.
cached_account() {
  local config
  config="$(dirname "$STEAMCMD_BIN")/config/config.vdf"

  [ -f "$config" ] || return 1

  # Inside the Accounts block, an account name is the only thing on its line - the keys
  # underneath it all carry a value on the same line. The tail drops the block header,
  # which is alone on its line too.
  sed -n '/"Accounts"/,/^\t\}/p' "$config" \
    | tail -n +2 \
    | grep -oE '^[[:space:]]*"[^"]+"[[:space:]]*$' \
    | tr -d ' \t"'
}

if [ -z "${STEAM_LOGIN:-}" ]; then
  ACCOUNTS=$(cached_account || true)
  COUNT=$(printf '%s' "$ACCOUNTS" | grep -c . || true)

  if [ "$COUNT" = "1" ]; then
    STEAM_LOGIN="$ACCOUNTS"
    echo "STEAM_LOGIN not set; using the account steamcmd has cached: $STEAM_LOGIN"
  elif [ "$COUNT" -gt 1 ] 2>/dev/null; then
    echo "error: steamcmd has more than one account cached. Set STEAM_LOGIN to the one to" >&2
    echo "       publish as. Cached:" >&2
    printf '         %s\n' $ACCOUNTS >&2
    exit 1
  else
    echo "error: no cached steamcmd account and STEAM_LOGIN is not set. Log in once with" >&2
    echo "         \"$STEAMCMD_BIN\" +login <account> +quit" >&2
    exit 1
  fi
fi

# steamcmd can only ask for a password when it owns the terminal. Run from MSBuild - which
# captures stdin - the prompt reads EOF, it submits an empty password, and the login fails
# with "Invalid Password" without ever pausing. Logging in once by hand caches the session
# and every later run is non-interactive.
#
# What is cached is a refresh token, not the password, and it expires - and is invalidated
# by signing in elsewhere, or by a password or Steam Guard change. So expect to repeat that
# interactive login every few days; it is not a sign anything is set up wrongly.
if [ ! -f "$(dirname "$STEAMCMD_BIN")/config/config.vdf" ] || ! grep -qi "ConnectCache\|WebToken" "$(dirname "$STEAMCMD_BIN")/config/config.vdf" 2>/dev/null; then
  echo
  echo "note: steamcmd has no cached login. If this fails with 'Invalid Password' without"
  echo "      prompting, run this once in a normal terminal window and then retry:"
  echo
  echo "          \"$STEAMCMD_BIN\" +login \"$STEAM_LOGIN\" +quit"
  echo
fi

"$STEAMCMD_BIN" +login "$STEAM_LOGIN" +workshop_build_item "$TMP_VDF" +quit

# --- afterwards -------------------------------------------------------------

# A first publish is given a fresh id, so copy it back into the template. On an update this
# writes the same id it already had.
FILE_ID=$(grep '"publishedfileid"' "$TMP_VDF_POSIX" | sed 's/.*"publishedfileid"[ \t]*"\([0-9]\+\)".*/\1/')

echo "published file ID: $FILE_ID"
sed -i 's/\("publishedfileid"[ \t]*"\)[0-9]\+"/\1'"$FILE_ID"'"/' "$BASE_VDF"

rm -f "$TMP_VDF_POSIX"

echo
echo "Check $HOME/steamcmd/logs/workshop_log.txt for what was actually uploaded."
echo "A run that changed the files says 'Uploaded new content ( ManifestID ... )'."
echo "Without that line only the preview and the text changed."
