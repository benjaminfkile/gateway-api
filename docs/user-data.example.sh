#!/bin/bash
# Complete instance user-data (tech-spec §6). Everything else that used to live in a
# few hundred lines of bootstrap bash is now testable C# run by the gateway's node
# bootstrap (GATEWAY_BOOTSTRAP_ENABLED=true) and reconciler.
set -euo pipefail
dnf install -y docker awscli amazon-cloudwatch-agent
systemctl enable --now docker
mkdir -p /opt/gateway
aws s3 cp s3://<artifact-bucket>/gateway-api-latest.tar.gz /opt/gateway/gateway-api.tar.gz
tar -xzf /opt/gateway/gateway-api.tar.gz -C /opt/gateway
/opt/gateway/install.sh
