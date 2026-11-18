using FmcgDms.Preprod.Tenancy;
using Microsoft.Data.SqlClient;

// Operator-only CLI. No Control Plane endpoints are exposed by PREPROD API.
try
{
    if(args.Length<2 || !Guid.TryParse(args[1],out var tenantId) || tenantId==Guid.Empty)
        throw new TenantLifecycleDeniedException("TENANT_OPERATOR_COMMAND_INVALID");
    string command=args[0];
    bool withRef=command is "start-reactivation" or "decommission"
        or "complete-decommission";
    if(args.Length!=(command=="request"?4:command=="approve"?5:withRef?3:2)
        || command is not ("request" or "approve" or "provision" or "verify"
            or "activate" or "suspend" or "revoke" or "start-reactivation"
            or "abort-reactivation" or "decommission" or "complete-decommission"))
        throw new TenantLifecycleDeniedException("TENANT_OPERATOR_COMMAND_INVALID");

    var address=Environment.GetEnvironmentVariable("APP02B4_VAULT_ADDR");
    // Fail closed before opening the privileged Vault token file or making HTTP.
    TenantOperatorAdmission.ValidateVaultAnchor(
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),address);
    var vault=new VaultTenantOperatorSecrets(address ?? "");
    var secrets=await vault.ReadOperatorAsync(command,CancellationToken.None);
    TenantOperatorAdmission.Validate(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        address,secrets.RegistryConnection,command=="provision"?secrets.MasterConnection:null);
    var registry=new TenantRegistrySql(secrets.RegistryConnection);
    TenantRegistryEntry result;
    switch(command)
    {
        case "request":
            result=await registry.RequestAsync(tenantId,args[2],args[3],CancellationToken.None);
            break;
        case "approve":
            result=await registry.ApproveAsync(tenantId,args[2],args[3],args[4],CancellationToken.None);
            break;
        case "provision":
            if(secrets.MasterConnection is null)
                throw new TenantLifecycleDeniedException("TENANT_DDL_PRINCIPAL_UNAVAILABLE");
            result=await new SqlTenantProvisioner(registry,secrets.MasterConnection)
                .ProvisionAsync(tenantId,CancellationToken.None);
            break;
        case "verify":
            var scoped=await vault.ReadProbeAsync(command,tenantId,CancellationToken.None);
            result=await new TenantProbeVerifier(registry)
                .VerifyAsync(tenantId,scoped,CancellationToken.None);
            break;
        case "activate":
            var verified=await registry.GetAsync(tenantId,CancellationToken.None)
                ?? throw new TenantLifecycleDeniedException("TENANT_NOT_FOUND");
            result=await registry.TransitionAsync(tenantId,"ACTIVATE",verified.RowVersion,
                null,CancellationToken.None);
            break;
        default:
            var action=command switch
            {
                "suspend"=>"SUSPEND",
                "revoke"=>"REVOKE",
                "start-reactivation"=>"START_REACTIVATION",
                "abort-reactivation"=>"ABORT_REACTIVATION",
                "decommission"=>"DECOMMISSION",
                "complete-decommission"=>"COMPLETE_DECOMMISSION",
                _=>throw new TenantLifecycleDeniedException("TENANT_OPERATOR_COMMAND_INVALID")
            };
            result=await registry.TransitionAsync(tenantId,action,null,
                withRef?args[2]:null,CancellationToken.None);
            break;
    }
    Console.WriteLine($"B4_OPERATOR_RESULT tenant={result.TenantId:D} state={result.State} epoch={result.SecurityEpoch}");
}
catch(Exception ex)
{
    var code=ex is TenantLifecycleDeniedException denial?denial.Code:
        ex is SqlException?"TENANT_SQL_AUTHORITY_OR_STATE_DENIED":"TENANT_OPERATOR_FAILED";
    Console.Error.WriteLine("B4_OPERATOR_DENIED code="+code);
    Environment.ExitCode=1;
}
