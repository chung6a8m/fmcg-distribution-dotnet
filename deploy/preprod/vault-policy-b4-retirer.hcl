# APP-02B.4 / OWN-APP02B4-SEC-01 — independent DBA retirement.
# TEMPLATE ONLY; no secrets or token issuance, not deployed to PREPROD.
# An operator must replace the GUID with exactly ONE approved TenantId,
# and bind this policy to a DIFFERENT DBA-controlled token/principal than
# tenant-provisioning/finalizer/<id> and all API/Worker operators.
# 
# path "preprod/data/tenant-provisioning/retirer/<approved-tenant-guid-N>" {
#   capabilities = ["read"]
# }
# Never use "retirer/*", "tenant-provisioning/*" or a shared token.
