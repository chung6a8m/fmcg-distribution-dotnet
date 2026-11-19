# APP-02B.4 — approver Vault KV-v2 role (source policy template, NOT deployed).
# Neither API nor Worker token may have these capabilities.
path "preprod/data/tenant-provisioning/approver" {
  capabilities = ["read"]
}
