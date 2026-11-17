# B.2 independent Vault operator applies this read-only policy to Worker identity.
# This cannot read API material or list keys/tenants.
path "preprod/data/runtime/worker" {
  capabilities = ["read"]
}
