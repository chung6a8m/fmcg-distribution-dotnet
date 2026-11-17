# Intentionally NON-PERSISTENT and NOT hardened for real hosting.
# Server mode (not -dev): explicit TLS, init/unseal, ACL and audit testing.
disable_mlock = true
ui = false
api_addr = "https://vault.private.example.invalid:8200"
storage "inmem" {}
listener "tcp" {
  address = "0.0.0.0:8200"
  tls_cert_file = "/vault/tls/vault.crt"
  tls_key_file = "/vault/tls/vault.key"
  tls_min_version = "tls12"
}
