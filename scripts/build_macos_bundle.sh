#!/usr/bin/env bash
# =============================================================================
# build_macos_bundle.sh — Build a double-clickable Lunima.app bundle
#
# Creates a self-contained macOS .app bundle from a dotnet publish output.
# The bundle is UNSIGNED and UNNOTARIZED — suitable for local use and
# distribution to machines where the developer controls quarantine removal.
#
# Usage:
#   scripts/build_macos_bundle.sh [RID] [--output DIR]
#
# Arguments:
#   RID          Runtime identifier (default: osx-arm64; also: osx-x64)
#   --output DIR Output directory for Lunima.app (default: <repo>/dist)
#
# After distributing to another Mac, the recipient must run:
#   xattr -dr com.apple.quarantine /path/to/Lunima.app
#
# Examples:
#   scripts/build_macos_bundle.sh
#   scripts/build_macos_bundle.sh osx-x64
#   scripts/build_macos_bundle.sh osx-arm64 --output ~/Desktop
# =============================================================================

set -euo pipefail

# ---------------------------------------------------------------------------
# Resolve repo root from the script's own location (cwd-independent)
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# ---------------------------------------------------------------------------
# Defaults
# ---------------------------------------------------------------------------
RID="osx-arm64"
OUTPUT_DIR="${REPO_ROOT}/dist"

# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    osx-arm64|osx-x64)
      RID="$1"
      shift
      ;;
    --output)
      if [[ -z "${2-}" ]]; then
        echo "ERROR: --output requires a directory argument." >&2
        exit 1
      fi
      OUTPUT_DIR="$2"
      shift 2
      ;;
    *)
      echo "ERROR: Unknown argument: $1" >&2
      echo "Usage: $0 [osx-arm64|osx-x64] [--output DIR]" >&2
      exit 1
      ;;
  esac
done

# ---------------------------------------------------------------------------
# Derived paths
# ---------------------------------------------------------------------------
PROJECT="${REPO_ROOT}/CAP.Desktop/CAP.Desktop.csproj"
PUBLISH_DIR="${REPO_ROOT}/obj/publish-${RID}"
APP_BUNDLE="${OUTPUT_DIR}/Lunima.app"
CONTENTS_DIR="${APP_BUNDLE}/Contents"
MACOS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"
EXECUTABLE_NAME="CAP.Desktop"

# ---------------------------------------------------------------------------
# Read <Version> from the .csproj; fall back to 0.7.0
# ---------------------------------------------------------------------------
if command -v xmllint &>/dev/null; then
  APP_VERSION="$(xmllint --xpath 'string(//Version)' "${PROJECT}" 2>/dev/null || true)"
else
  # Pure-bash XML extraction — works without xmllint
  APP_VERSION="$(grep -oE '<Version>[^<]+</Version>' "${PROJECT}" | sed 's|<Version>||;s|</Version>||' || true)"
fi
APP_VERSION="${APP_VERSION:-0.7.0}"
APP_VERSION="${APP_VERSION//[[:space:]]/}"  # strip any whitespace

# ---------------------------------------------------------------------------
# Banner
# ---------------------------------------------------------------------------
echo "============================================================"
echo "  Lunima macOS App Bundle Builder"
echo "============================================================"
echo "  Repo root   : ${REPO_ROOT}"
echo "  Project     : ${PROJECT}"
echo "  RID         : ${RID}"
echo "  Version     : ${APP_VERSION}"
echo "  Output dir  : ${OUTPUT_DIR}"
echo "  App bundle  : ${APP_BUNDLE}"
echo "============================================================"
echo ""

# ---------------------------------------------------------------------------
# Step 1 — dotnet publish (self-contained folder, NOT single-file)
# ---------------------------------------------------------------------------
echo "[1/5] Publishing ${EXECUTABLE_NAME} for ${RID}..."
dotnet publish "${PROJECT}" \
  -c Release \
  -r "${RID}" \
  --self-contained \
  -p:PublishSingleFile=false \
  -o "${PUBLISH_DIR}"
echo "      Publish output: ${PUBLISH_DIR}"

# ---------------------------------------------------------------------------
# Step 2 — Create .app bundle layout
# ---------------------------------------------------------------------------
echo "[2/5] Creating bundle layout at ${APP_BUNDLE}..."
rm -rf "${APP_BUNDLE}"
mkdir -p "${MACOS_DIR}"
mkdir -p "${RESOURCES_DIR}"

# Copy entire publish output into Contents/MacOS/
cp -R "${PUBLISH_DIR}/." "${MACOS_DIR}/"

# Ensure the apphost executable is executable
if [[ -f "${MACOS_DIR}/${EXECUTABLE_NAME}" ]]; then
  chmod +x "${MACOS_DIR}/${EXECUTABLE_NAME}"
  echo "      chmod +x ${MACOS_DIR}/${EXECUTABLE_NAME}"
else
  echo "ERROR: Expected executable not found: ${MACOS_DIR}/${EXECUTABLE_NAME}" >&2
  echo "       Contents of MacOS dir:" >&2
  ls "${MACOS_DIR}" >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# Step 3 — Write Contents/Info.plist
# ---------------------------------------------------------------------------
echo "[3/5] Writing Info.plist (version ${APP_VERSION})..."
cat > "${CONTENTS_DIR}/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>Lunima</string>
  <key>CFBundleDisplayName</key>
  <string>Lunima</string>
  <key>CFBundleIdentifier</key>
  <string>com.lunima.app</string>
  <key>CFBundleExecutable</key>
  <string>CAP.Desktop</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${APP_VERSION}</string>
  <key>CFBundleVersion</key>
  <string>${APP_VERSION}</string>
  <key>LSMinimumSystemVersion</key>
  <string>11.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>CFBundleIconFile</key>
  <string>Lunima.icns</string>
</dict>
</plist>
PLIST
echo "      Info.plist written."

# ---------------------------------------------------------------------------
# Step 4 — Icon handling (best-effort; never fails the build)
# ---------------------------------------------------------------------------
echo "[4/5] Handling application icon..."

ICNS_SOURCE="${REPO_ROOT}/Installer/LunimaIcon.icns"
ICNS_DEST="${RESOURCES_DIR}/Lunima.icns"

if [[ -f "${ICNS_SOURCE}" ]]; then
  cp "${ICNS_SOURCE}" "${ICNS_DEST}"
  echo "      Icon copied from ${ICNS_SOURCE}."
else
  # generate_icon.py has a build_icns() function; try to invoke it via Python
  GENERATE_ICON="${SCRIPT_DIR}/generate_icon.py"
  ICON_GENERATED=false

  if [[ -f "${GENERATE_ICON}" ]]; then
    echo "      ${ICNS_SOURCE} not found; attempting to generate icon via generate_icon.py..."
    # Call build_icns() directly — the CLI only exposes --output for .ico,
    # but the function is importable via a one-liner.
    if python3 - <<PYEOF 2>/dev/null; then
import sys
sys.path.insert(0, "${SCRIPT_DIR}")
from generate_icon import build_icns
from pathlib import Path
build_icns(Path("${ICNS_DEST}"))
PYEOF
      ICON_GENERATED=true
      echo "      Icon generated at ${ICNS_DEST}."
    else
      echo "      NOTE: Icon generation failed (Pillow may not be installed). Proceeding without icon."
    fi
  fi

  if [[ "${ICON_GENERATED}" == false ]]; then
    echo "      NOTE: No icon file available. The app will run without a custom icon."
    echo "            To add one later, copy a LunimaIcon.icns to Installer/ and re-run this script."
  fi
fi

# ---------------------------------------------------------------------------
# Step 5 — Final summary
# ---------------------------------------------------------------------------
echo ""
echo "[5/5] Build complete."
echo ""
echo "============================================================"
echo "  SUCCESS: Lunima.app built"
echo "============================================================"
echo "  App bundle : ${APP_BUNDLE}"
echo "  Version    : ${APP_VERSION}"
echo "  RID        : ${RID}"
echo ""
echo "  WARNING: This app is UNSIGNED and UNNOTARIZED."
echo "  It will open normally when launched on the machine that built it."
echo ""
echo "  To distribute to other Macs, recipients must remove quarantine:"
echo "    xattr -dr com.apple.quarantine \"${APP_BUNDLE}\""
echo "============================================================"
