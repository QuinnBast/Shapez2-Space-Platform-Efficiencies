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

# Set STEAM_LOGIN to your own Steam account name before publishing. The sample this was
# copied from hard-coded a tobspr developer account.
"$STEAMCMD_BIN" +login "${STEAM_LOGIN:?set STEAM_LOGIN to your Steam account name}" +workshop_build_item "$TMP_VDF" +quit;

# Copy published file id back
cat Steam\\base.tmp.vdf

# Grab published file id 
FILE_ID=$(grep '"publishedfileid"' Steam\\base.tmp.vdf | sed 's/.*"publishedfileid"[ \t]*"\([0-9]\+\)".*/\1/')

# Updating original file with new published file ID
echo "New published file ID: $FILE_ID"
sed -i 's/\("publishedfileid"[ \t]*"\)[0-9]\+"/\1'"$FILE_ID"'"/'  Steam\\base.vdf

# Clean temporary files
rm Steam\\base.tmp.vdf