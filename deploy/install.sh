#!/usr/bin/env bash
#
# install.sh — install/upgrade gateway-api on the local box from the unpacked
# release tarball (tech-spec §6). Idempotent: safe to re-run. Invoked from the tiny
# instance user-data (see docs/user-data.example.sh) after the tarball is extracted,
# or by hand during a manual deploy.
#
# Layout it creates:
#   /opt/gateway/gateway-api              the self-contained binary
#   /etc/systemd/system/gateway-api.service
#   /etc/gateway-api/env                  environment file (created if absent; never overwritten)
#
set -euo pipefail

SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="/opt/gateway"
UNIT_DIR="/etc/systemd/system"
ENV_DIR="/etc/gateway-api"
ENV_FILE="${ENV_DIR}/env"

echo "Installing gateway-api from ${SRC_DIR}"

install -d -m 0755 "${INSTALL_DIR}" "${ENV_DIR}"

# Binary: install with execute bit.
install -m 0755 "${SRC_DIR}/gateway-api" "${INSTALL_DIR}/gateway-api"

# systemd unit.
install -m 0644 "${SRC_DIR}/gateway-api.service" "${UNIT_DIR}/gateway-api.service"

# Environment file: seed a template on first install only — never clobber an
# operator's real config on upgrade.
if [[ ! -f "${ENV_FILE}" ]]; then
  cat > "${ENV_FILE}" <<'EOF'
# gateway-api environment (tech-spec §6). Uncomment and set as needed.
# ASPNETCORE_URLS=http://0.0.0.0:80
# GATEWAY_BOOTSTRAP_ENABLED=true
# GATEWAY_RECONCILER_ENABLED=true
# GATEWAY_DB_CONNECTION=Host=...;Database=gateway;Username=...;Password=...
# GATEWAY_COGNITO_AUTHORITY=https://cognito-idp.<region>.amazonaws.com/<pool-id>
# GATEWAY_REDIS_ENDPOINT=<redis-host>:6379
# GATEWAY_INTERNAL_BIND=http://172.17.0.1:8080
EOF
  chmod 0640 "${ENV_FILE}"
  echo "Wrote template ${ENV_FILE} — edit it before first start."
fi

systemctl daemon-reload
systemctl enable --now gateway-api

echo "gateway-api installed and started."
