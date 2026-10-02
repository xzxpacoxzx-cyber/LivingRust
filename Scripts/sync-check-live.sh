#!/usr/bin/env bash
# Pulls the live LivingRust.cszip from Cybrancee over SFTP and diffs its
# .cs files against local source, so drift between "what's actually
# running" and "what's in this folder/git" shows up as a one-command
# check instead of a manual download+unzip+compare (2026-10-02, after a
# live server had moved ~8 days ahead of local in another session and
# almost got silently overwritten by a local redeploy).
#
# Never reads or stores a password: sftp prompts for it live, interactively,
# every run. Run from Git Bash: Scripts/sync-check-live.sh
#
# Usage:
#   Scripts/sync-check-live.sh            # just show the diff summary
#   Scripts/sync-check-live.sh --apply     # also copy live files over local
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

ENV_FILE="Scripts/cybrancee.env"
if [ ! -f "$ENV_FILE" ]; then
    echo "Missing $ENV_FILE - copy Scripts/cybrancee.env.example to $ENV_FILE and fill in CYBRANCEE_USER (and check CYBRANCEE_REMOTE_CSZIP)." >&2
    exit 1
fi
# shellcheck disable=SC1090
source "$ENV_FILE"

: "${CYBRANCEE_HOST:?set in $ENV_FILE}"
: "${CYBRANCEE_PORT:?set in $ENV_FILE}"
: "${CYBRANCEE_USER:?set in $ENV_FILE}"
: "${CYBRANCEE_REMOTE_CSZIP:?set in $ENV_FILE}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "Connecting to ${CYBRANCEE_USER}@${CYBRANCEE_HOST}:${CYBRANCEE_PORT} - you'll be prompted for the password..."
sftp -P "$CYBRANCEE_PORT" -o StrictHostKeyChecking=accept-new \
    "${CYBRANCEE_USER}@${CYBRANCEE_HOST}" \
    <<EOF
get "${CYBRANCEE_REMOTE_CSZIP}" "${WORK_DIR}/LivingRust_live.zip"
EOF

if [ ! -f "${WORK_DIR}/LivingRust_live.zip" ]; then
    echo "Download failed - check CYBRANCEE_REMOTE_CSZIP in $ENV_FILE against the real path (connect with plain 'sftp' to look around)." >&2
    exit 1
fi

mkdir -p "${WORK_DIR}/live"
powershell -NoProfile -Command \
    "Expand-Archive -Path '$(cygpath -w "${WORK_DIR}/LivingRust_live.zip")' -DestinationPath '$(cygpath -w "${WORK_DIR}/live")' -Force"

echo
echo "=== Files on live but not in local source tree ==="
find . -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" -exec basename {} \; | sort > "${WORK_DIR}/local_files.txt"
(cd "${WORK_DIR}/live" && ls ./*.cs 2>/dev/null | xargs -n1 basename | sort) > "${WORK_DIR}/live_files.txt"
comm -23 "${WORK_DIR}/live_files.txt" "${WORK_DIR}/local_files.txt" || true

echo
echo "=== Files differing between live and local (line counts) ==="
CHANGED=0
find . -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" > "${WORK_DIR}/local_paths.txt"
while read -r p; do
    fn="$(basename "$p")"
    live_f="${WORK_DIR}/live/$fn"
    if [ -f "$live_f" ] && ! diff -q "$p" "$live_f" > /dev/null 2>&1; then
        lc=$(diff "$p" "$live_f" | grep -c '^[<>]')
        echo "$fn : $lc differing lines"
        CHANGED=1
    fi
done < "${WORK_DIR}/local_paths.txt"

if [ "$CHANGED" -eq 0 ]; then
    echo "(none - local matches live)"
fi

if [ "${1:-}" = "--apply" ]; then
    echo
    echo "Applying: copying live .cs files over local source..."
    while read -r p; do
        fn="$(basename "$p")"
        live_f="${WORK_DIR}/live/$fn"
        [ -f "$live_f" ] && cp "$live_f" "$p"
    done < "${WORK_DIR}/local_paths.txt"
    # New files on live with no local home get dropped into Plugin/ (every
    # LivingRust.*.cs file in this project already lives there).
    comm -23 "${WORK_DIR}/live_files.txt" "${WORK_DIR}/local_files.txt" | while read -r fn; do
        cp "${WORK_DIR}/live/$fn" "Plugin/$fn"
        echo "Added new file: Plugin/$fn"
    done
    echo "Done - review with 'git status'/'git diff', build with 'dotnet build -p:NoDeploy=true', then commit."
else
    echo
    echo "(dry run - rerun with --apply to copy these changes into local source)"
fi
