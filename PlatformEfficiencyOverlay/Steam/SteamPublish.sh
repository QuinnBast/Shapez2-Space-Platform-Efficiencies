#!/usr/bin/env bash

CONTENT_PATH=$1

# Prints the current working directory into the variable while converting the POSIX-style path to windows-style path
# Converts /c/ to C:/
CURRENT_DIR=$(cygpath -w "$PWD")

# Composes the location of the preview image
PREVIEW_IMG=$CURRENT_DIR\\Steam\\preview.png

# Adjust paths to use double backlashes
CONTENT_PATH="${CONTENT_PATH//\\/\\\\}"
PREVIEW_IMG="${PREVIEW_IMG//\//\\}"
PREVIEW_IMG="${PREVIEW_IMG//\\/\\\\}"

echo "CONTENT_PATH: $CONTENT_PATH"
echo "PREVIEW_IMG: $PREVIEW_IMG"

export CONTENT_PATH
export PREVIEW_IMG

# Adjust temporary .vdf with absolute paths for the content and the preview image
envsubst < Steam\\base.vdf > Steam\\base.tmp.vdf

# Log the final version
cat Steam\\base.tmp.vdf

TMP_VDF=$CURRENT_DIR\\Steam\\base.tmp.vdf

# Find steamcmd. It is usually not on PATH, and it has to live somewhere writable
# because it self-updates into its own folder - which rules out Program Files.
find_steamcmd() {
  if [ -n "${STEAMCMD:-}" ] && [ -x "$STEAMCMD" ]; then
    printf '%s' "$STEAMCMD"
    return 0
  fi

  if command -v steamcmd >/dev/null 2>&1; then
    command -v steamcmd
    return 0
  fi

  for candidate in     "$HOME/steamcmd/steamcmd.exe"     "C:/steamcmd/steamcmd.exe"     "${PROGRAMFILES:-C:/Program Files}/SteamCMD/steamcmd.exe"     "${LOCALAPPDATA:-}/SteamCMD/steamcmd.exe"
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
# config. STEAM_LOGIN overrides it, and is only needed when more than one account is
# cached.
cached_account() {
  local config
  config="$(dirname "$STEAMCMD_BIN")/config/config.vdf"

  [ -f "$config" ] || return 1

  # Inside the Accounts block, an account name is the only thing on its line - the keys
  # underneath it all carry a value on the same line. The tail drops the
  # block header, which is alone on its line too.
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
if [ ! -f "$(dirname "$STEAMCMD_BIN")/config/config.vdf" ]    || ! grep -qi "ConnectCache\|WebToken" "$(dirname "$STEAMCMD_BIN")/config/config.vdf" 2>/dev/null; then
  echo
  echo "note: steamcmd has no cached login. If this fails with 'Invalid Password' without"
  echo "      prompting, run this once in a normal terminal window and then retry:"
  echo
  echo "          \"$STEAMCMD_BIN\" +login \"$STEAM_LOGIN\" +quit"
  echo
fi

# The sample this was copied from hard-coded a tobspr developer account; the account now
# comes from STEAM_LOGIN or from steamcmd's own cache, resolved above.
"$STEAMCMD_BIN" +login "$STEAM_LOGIN" +workshop_build_item "$TMP_VDF" +quit;

# Copy published file id back
cat Steam\\base.tmp.vdf

# Grab published file id 
FILE_ID=$(grep '"publishedfileid"' Steam\\base.tmp.vdf | sed 's/.*"publishedfileid"[ \t]*"\([0-9]\+\)".*/\1/')

# Updating original file with new published file ID
echo "New published file ID: $FILE_ID"
sed -i 's/\("publishedfileid"[ \t]*"\)[0-9]\+"/\1'"$FILE_ID"'"/'  Steam\\base.vdf

# Clean temporary files
rm Steam\\base.tmp.vdf