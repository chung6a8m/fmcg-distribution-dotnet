# APP-02B.4 — verifier Vault KV-v2 role (source policy template, NOT deployed).
# Neither API nor Worker token may have these capabilities.
path "preprod/data/tenant-provisioning/verifier" {
  capabilities = ["read"]
}

# Do NOT use "probe/*" in a deployed policy. The operator must append one
# exact approved tenant GUID-N path to a per-tenant verifier policy only after
# a separate reviewed mapping/authorization.
# path "preprod/data/tenant-provisioning/probe/<approved-tenant-guid-N>" {
#   capabilities = ["read"]
# }
