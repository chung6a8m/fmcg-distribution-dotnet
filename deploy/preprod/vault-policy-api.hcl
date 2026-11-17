# B.2 independent Vault operator applies this read-only policy to API identity.
# Enable Vault audit on the Vault server BEFORE testing. No list/admin grants.
path "preprod/data/runtime/api" {
  capabilities = ["read"]
}
