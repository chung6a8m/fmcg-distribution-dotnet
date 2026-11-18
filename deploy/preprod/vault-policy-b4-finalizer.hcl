# APP-02B.4 per-tenant finalizer Vault KV-v2 policy template.
# NOT DEPLOYED: the DBA must instantiate one policy per exact TenantId (GUID-N),
# independently approve its SQL principal and bind the token to one operator session.
# Never deploy finalizer/*, tenant-provisioning/*, or a general finalizer role secret.
# Example (replace ONLY after independent ticket + source/migration review):
# path "preprod/data/tenant-provisioning/finalizer/<approved-tenant-guid-N>" {
#   capabilities = ["read"]
# }
# API, Worker, requester, approver, provisioner and verifier MUST NOT have access.
