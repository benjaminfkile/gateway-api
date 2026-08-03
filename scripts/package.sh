#!/usr/bin/env bash
#
# package.sh — build a self-contained single-file gateway-api release and bundle it
# with the systemd unit and install script into
#   gateway-api-<version>-linux-arm64.tar.gz
# (tech-spec §6, §10 Phase 0). The artifact is uploaded to the object-storage bucket
# and pulled by instance user-data (docs/user-data.example.sh).
#
# NOTE: this is a build/release tool run in CI or on a dev box — it is NOT exercised
# by `dotnet test` (which has no publish toolchain wired for cross-arch output).
#
set -euo pipefail

RID="linux-arm64"
CONFIG="Release"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="${REPO_ROOT}/src/Gateway.Api/Gateway.Api.csproj"
DEPLOY_DIR="${REPO_ROOT}/deploy"
OUT_DIR="${REPO_ROOT}/artifacts"

# Version: first arg, else GITHUB_REF_NAME/tag, else a dev stamp.
VERSION="${1:-${GITHUB_REF_NAME:-0.0.0-dev}}"
VERSION="${VERSION#v}" # strip a leading 'v' from a git tag

STAGE="$(mktemp -d)"
trap 'rm -rf "${STAGE}"' EXIT

echo "Publishing gateway-api ${VERSION} (${CONFIG}, ${RID}, self-contained single-file)…"
dotnet publish "${PROJECT}" \
  -c "${CONFIG}" \
  -r "${RID}" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:Version="${VERSION}" \
  -o "${STAGE}/publish"

# Assemble the bundle: binary + unit + install script (tech-spec §6).
install -m 0755 "${STAGE}/publish/Gateway.Api" "${STAGE}/gateway-api"
install -m 0644 "${DEPLOY_DIR}/gateway-api.service" "${STAGE}/gateway-api.service"
install -m 0755 "${DEPLOY_DIR}/install.sh" "${STAGE}/install.sh"

mkdir -p "${OUT_DIR}"
TARBALL="${OUT_DIR}/gateway-api-${VERSION}-${RID}.tar.gz"
tar -czf "${TARBALL}" -C "${STAGE}" gateway-api gateway-api.service install.sh

echo "Wrote ${TARBALL}"
