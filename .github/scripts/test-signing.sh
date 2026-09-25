#!/usr/bin/env bash
#
# Builds an MSI on a non-Windows machine and signs it there.
#
# This is the claim that most needs proving: not only that a package can be
# produced without Windows, but that it can be signed without Windows too.
# osslsigncode is the signer that exists on macOS and Linux, and it is the same
# binary in both places.
set -euo pipefail

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cli="dotnet run --project src/MsiForge.App -c Release --no-build --"

echo "=== what can this machine do? ==="
$cli doctor
echo

echo "=== build a package here ==="
mkdir -p "$work/payload/sub"
printf 'signed payload' > "$work/payload/readme.txt"
printf 'nested' > "$work/payload/sub/nested.txt"

cat > "$work/project.json" <<JSON
{
  "ProductName": "MsiForge Signing CI",
  "Manufacturer": "MsiForge",
  "Version": "1.0.0",
  "UpgradeCode": "6c2b9f41-8d13-4e75-a0b6-3f1c7d8e2a95",
  "Platform": "X64",
  "Folders": [ { "SourceDir": "$work/payload" } ],
  "Signing": {
    "Enabled": true,
    "CertificatePath": "$work/ci.pfx",
    "PasswordEnvironmentVariable": "MSIFORGE_CI_PFX_PASSWORD",
    "TimestampUrl": "http://timestamp.digicert.com"
  }
}
JSON

$cli build "$work/project.json" "$work/out.msi"
unsigned=$(wc -c < "$work/out.msi")
echo "unsigned: $unsigned bytes"
echo

echo "=== a throwaway signing certificate ==="
openssl req -x509 -newkey rsa:2048 -sha256 -days 2 -nodes \
    -keyout "$work/key.pem" -out "$work/cert.pem" \
    -subj "/CN=MsiForge CI Signing" \
    -addext "keyUsage=digitalSignature" \
    -addext "extendedKeyUsage=codeSigning" 2>/dev/null
openssl pkcs12 -export -out "$work/ci.pfx" \
    -inkey "$work/key.pem" -in "$work/cert.pem" \
    -passout "pass:${MSIFORGE_CI_PFX_PASSWORD}"
echo "pfx: $(wc -c < "$work/ci.pfx") bytes"
echo

echo "=== sign it ==="
$cli sign "$work/out.msi" "$work/project.json"

signed=$(wc -c < "$work/out.msi")
echo "signed: $signed bytes"
if [ "$signed" -le "$unsigned" ]; then
    echo "FAIL: the file did not grow, so no signature was embedded"
    exit 1
fi

echo
echo "=== is the signature really there? ==="
# osslsigncode verify fails on chain trust for a self-signed certificate, which
# is expected; what matters is that it finds and parses a signature rather than
# reporting none at all.
set +e
verify_output="$(osslsigncode verify -in "$work/out.msi" 2>&1)"
set -e
echo "$verify_output" | tail -n 12

if echo "$verify_output" | grep -qiE "No signature found|MSI file has no signature"; then
    echo "FAIL: no signature in the file"
    exit 1
fi
if ! echo "$verify_output" | grep -qiE "Signer|Current PE checksum|Message digest algorithm|Timestamp"; then
    echo "FAIL: output does not describe a signature"
    exit 1
fi

echo
echo "=== the signed package still reads correctly ==="
$cli inspect "$work/out.msi" | head -n 10

echo
echo "built and signed an MSI on $(uname -s), with no Windows involved"
