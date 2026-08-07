#!/bin/bash
# Complete instance user-data (tech-spec §6). Everything else that used to live in a
# few hundred lines of bootstrap bash is now testable C# run by the gateway's node
# bootstrap (GATEWAY_BOOTSTRAP_ENABLED=true) and reconciler.
set -euo pipefail
# libicu: the self-contained .NET binary FailFasts at startup without ICU.
dnf install -y docker awscli amazon-cloudwatch-agent libicu
systemctl enable --now docker
# Extract to a staging dir, NOT /opt/gateway: install.sh installs the binary
# INTO /opt/gateway and errors ("same file") if the tarball was unpacked there.
mkdir -p /opt/gateway /opt/gateway-dist
aws s3 cp s3://<artifact-bucket>/gateway-api-latest.tar.gz /opt/gateway/gateway-api.tar.gz
tar -xzf /opt/gateway/gateway-api.tar.gz -C /opt/gateway-dist
/opt/gateway-dist/install.sh
