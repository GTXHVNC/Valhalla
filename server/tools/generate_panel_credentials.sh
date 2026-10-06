#!/usr/bin/env bash
set -euo pipefail
umask 077

OUT_DIR="${1:-/var/lib/valhalla/panel}"
SERVER_NAME="${2:-valhalla-relay}"
mkdir -p "$OUT_DIR"

CA_KEY="$OUT_DIR/ca.key"
CA_CERT="$OUT_DIR/ca.crt"
SERVER_KEY="$OUT_DIR/server.key"
SERVER_CERT="$OUT_DIR/server.crt"
SECRET="$OUT_DIR/secret"

present=0
for f in "$CA_KEY" "$CA_CERT" "$SERVER_KEY" "$SERVER_CERT" "$SECRET"; do
  [[ -f "$f" ]] && present=$((present + 1))
done
if (( present == 5 )); then
  printf 'Valhalla panel credentials already exist in %s\n' "$OUT_DIR"
  exit 0
fi
if (( present != 0 )); then
  echo "Refusing to overwrite an incomplete Valhalla panel credential set in $OUT_DIR" >&2
  exit 1
fi

TMP_DIR="$OUT_DIR/.valhalla-panel-credentials.$$"
cleanup() { rm -rf "$TMP_DIR"; }
trap cleanup EXIT
mkdir "$TMP_DIR"

openssl req -x509 -newkey rsa:4096 -nodes -days 825 -sha256 \
  -subj "/CN=Valhalla Panel CA" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -keyout "$TMP_DIR/ca.key" -out "$TMP_DIR/ca.crt"

openssl req -new -newkey rsa:3072 -nodes -sha256 \
  -subj "/CN=$SERVER_NAME" \
  -keyout "$TMP_DIR/server.key" -out "$TMP_DIR/server.csr"

cat > "$TMP_DIR/server.ext" <<EXT
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:localhost,DNS:$SERVER_NAME
EXT

openssl x509 -req -days 825 -sha256 \
  -CA "$TMP_DIR/ca.crt" -CAkey "$TMP_DIR/ca.key" -CAcreateserial \
  -in "$TMP_DIR/server.csr" -out "$TMP_DIR/server.crt" -extfile "$TMP_DIR/server.ext"

openssl verify -CAfile "$TMP_DIR/ca.crt" "$TMP_DIR/server.crt"

mv "$TMP_DIR/ca.key" "$CA_KEY"
mv "$TMP_DIR/ca.crt" "$CA_CERT"
mv "$TMP_DIR/server.key" "$SERVER_KEY"
mv "$TMP_DIR/server.crt" "$SERVER_CERT"
python3 - "$SECRET" <<'PY'
import secrets
import sys
with open(sys.argv[1], "x", encoding="ascii") as f:
    f.write(secrets.token_hex(16) + "\n")
PY
chmod 600 "$CA_KEY" "$SERVER_KEY" "$SECRET"
chmod 644 "$CA_CERT" "$SERVER_CERT"
printf 'Created Valhalla panel CA/server credentials and panel secret in %s\n' "$OUT_DIR"
