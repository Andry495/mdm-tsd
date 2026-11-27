#!/bin/bash
set -euo pipefail
umask 077
cat > /tmp/portal-san.cnf << 'EOF'
[req]
distinguished_name = dn
req_extensions = v3
prompt = no
[dn]
CN = mdm-tsd
[v3]
subjectAltName = DNS:mdm-tsd
extendedKeyUsage = serverAuth,clientAuth
keyUsage = digitalSignature,keyEncipherment
EOF
openssl req -new -newkey rsa:2048 -nodes \
  -keyout /opt/tsd-portal/certs/portal.key.new \
  -out /tmp/portal.csr \
  -config /tmp/portal-san.cnf
chmod 644 /opt/tsd-portal/certs/portal.key.new /tmp/portal.csr
