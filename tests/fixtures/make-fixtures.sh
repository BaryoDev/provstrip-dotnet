#!/bin/bash
# Rebuilds every test image in this folder.
#
# The C2PA files are real C2PA, signed by c2patool with a certificate made here in a temp
# directory and deleted on exit. Its private key is never written into the repo. Everything else
# (EXIF, XMP, IPTC, ICC, broken files) is built byte by byte in make_fixtures.py, so no file names
# a real tool.
#
# Needs: c2patool, cjpeg (jpeg-turbo), cwebp (webp), openssl, python3.
#   brew install c2patool jpeg-turbo webp
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
for tool in c2patool cjpeg cwebp openssl python3; do
  command -v "$tool" >/dev/null || { echo "missing $tool (brew install c2patool jpeg-turbo webp)"; exit 1; }
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# c2patool refuses a self-signed signer, so this is a CA plus a leaf with the key usage, extended
# key usage and key identifiers the C2PA certificate profile asks for.
openssl ecparam -name prime256v1 -genkey -noout -out "$work/ca.key" 2>/dev/null
openssl req -x509 -new -key "$work/ca.key" -sha256 -days 3650 \
  -subj "/CN=provstrip fixture CA/O=provstrip-fixtures" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -out "$work/ca.pem" 2>/dev/null
openssl ecparam -name prime256v1 -genkey -noout -out "$work/leaf.ec" 2>/dev/null
openssl pkcs8 -topk8 -nocrypt -in "$work/leaf.ec" -out "$work/leaf.key"
openssl req -new -key "$work/leaf.key" -subj "/CN=provstrip fixture signer/O=provstrip-fixtures" \
  -out "$work/leaf.csr" 2>/dev/null
printf '%s\n' 'basicConstraints=critical,CA:FALSE' 'keyUsage=critical,digitalSignature' \
  'extendedKeyUsage=emailProtection' 'subjectKeyIdentifier=hash' 'authorityKeyIdentifier=keyid,issuer' \
  > "$work/leaf.cnf"
openssl x509 -req -in "$work/leaf.csr" -CA "$work/ca.pem" -CAkey "$work/ca.key" -CAcreateserial \
  -days 3650 -sha256 -extfile "$work/leaf.cnf" -out "$work/leaf.pem" 2>/dev/null
cat "$work/leaf.pem" "$work/ca.pem" > "$work/chain.pem"

python3 -I "$here/make_fixtures.py" "$here" "$work"
echo "fixtures written to $here"
