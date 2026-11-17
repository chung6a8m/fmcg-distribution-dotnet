using FmcgDms.Preprod.Tenancy;
using Xunit;

namespace FmcgDms.Preprod.Tenancy.Tests;

public sealed class TenantSafetyTests
{
    [Fact]
    public void Database_name_is_deterministic_and_contains_no_caller_sql()
    {
        var id=Guid.Parse("20ad96e7-d561-4d5d-9f79-3b8b1b1ea402");
        Assert.Equal("FmcgPpT_20ad96e7d5614d5d9f793b8b1b1ea402",
            TenantRegistryEntry.ExpectedDatabase(id));
        Assert.DoesNotContain("-",TenantRegistryEntry.ExpectedDatabase(id));
    }

    [Theory]
    [InlineData(null,"https://vault.internal.example",false)]
    [InlineData("Production","https://vault.internal.example",false)]
    [InlineData("Preprod","http://vault.internal.example",false)]
    [InlineData("Preprod","https://localhost",false)]
    [InlineData("Preprod","https://8.8.8.8",false)]
    [InlineData("Preprod","https://vault.internal.example",true)]
    public void Operator_requires_nonproduction_and_private_tls_vault(
        string? env,string address,bool success)
    {
        const string registry="Server=sql.internal;Database=control_plane;Encrypt=True;TrustServerCertificate=False;Integrated Security=True";
        if(success) TenantOperatorAdmission.Validate(env,address,registry);
        else Assert.Throws<TenantLifecycleDeniedException>(()=>
            TenantOperatorAdmission.Validate(env,address,registry));
    }

    [Theory]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=control_plane;Integrated Security=True")]
    [InlineData("Server=localhost;Database=control_plane;Encrypt=True")]
    [InlineData("Server=sql.internal;Database=master;Encrypt=True")]
    [InlineData("Server=sql.internal;Database=control_plane;Encrypt=False")]
    [InlineData("Server=sql.internal;Database=control_plane;Encrypt=True;TrustServerCertificate=True")]
    public void Operator_refuses_localdev_master_and_tls_downgrades(string sql)
    {
        Assert.Throws<TenantLifecycleDeniedException>(()=>
            TenantOperatorAdmission.Validate("Preprod","https://vault.internal.example",sql));
    }

    [Fact]
    public void Master_and_control_cannot_share_sql_principal_or_instance()
    {
        const string control="Server=sql.internal;Database=control;Encrypt=True;User Id=cp;Password=not-a-real-password";
        const string same="Server=sql.internal;Database=master;Encrypt=True;User Id=cp;Password=not-a-real-password";
        const string peer="Server=peer.internal;Database=master;Encrypt=True;User Id=prov;Password=not-a-real-password";
        Assert.Throws<TenantLifecycleDeniedException>(()=>
            TenantOperatorAdmission.Validate("Preprod","https://vault.internal.example",control,same));
        Assert.Throws<TenantLifecycleDeniedException>(()=>
            TenantOperatorAdmission.Validate("Preprod","https://vault.internal.example",control,peer));
    }

    [Fact]
    public void Non_active_registry_states_are_always_non_routable()
    {
        foreach(var state in new[]{"REQUESTED","APPROVED","PROVISIONING","FAILED_RETRYABLE",
            "VERIFYING","SUSPENDED","REACTIVATION_VERIFYING","DECOMMISSIONING",
            "DECOMMISSIONED"})
        {
            var record=new TenantRegistryEntry(Guid.NewGuid(),"",Guid.NewGuid(),1,1,
                state,new byte[8],null,null,null,null,null);
            Assert.False(record.IsRoutable);
        }
        Assert.True(new TenantRegistryEntry(Guid.NewGuid(),"",Guid.NewGuid(),1,1,
            "ACTIVE",new byte[8],null,null,null,null,null).IsRoutable);
    }

    [Fact]
    public async Task Probe_fails_before_opening_any_unknown_tenant()
    {
        // A missing registry connection must never be caught as "healthy".
        var registry=new TenantRegistrySql(
            "Server=127.0.0.1,1;Database=missing;Connect Timeout=1;TrustServerCertificate=False;Encrypt=True");
        await Assert.ThrowsAnyAsync<Exception>(async ()=>
            await new TenantProbeVerifier(registry)
                .VerifyAsync(Guid.NewGuid(),"",TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Vault_endpoint_is_pinned_before_any_operator_token_is_read()
    {
        var file=Path.GetTempFileName();
        try
        {
            File.WriteAllText(file,"https://vault.internal.example/");
            TenantOperatorAdmission.ValidateVaultAnchor(
                "Preprod","https://vault.internal.example",file);
            Assert.Throws<TenantLifecycleDeniedException>(()=>
                TenantOperatorAdmission.ValidateVaultAnchor(
                    "Preprod","https://attacker.example",file));
            Assert.Throws<TenantLifecycleDeniedException>(()=>
                TenantOperatorAdmission.ValidateVaultAnchor(
                    "Production","https://vault.internal.example",file));
            Assert.Throws<TenantLifecycleDeniedException>(()=>
                TenantOperatorAdmission.ValidateVaultAnchor(
                    "Preprod","http://vault.internal.example",file));
        }
        finally { File.Delete(file); }
    }


    [Theory]
    [InlineData("request","requester")]
    [InlineData("approve","approver")]
    [InlineData("activate","approver")]
    [InlineData("start-reactivation","approver")]
    [InlineData("provision","provisioner")]
    [InlineData("verify","verifier")]
    [InlineData("suspend","security")]
    [InlineData("revoke","security")]
    [InlineData("complete-decommission","verifier")]
    public void Vault_operator_roles_are_fixed_and_separated(string action,string role)
    {
        Assert.Equal(role,VaultTenantOperatorSecrets.SecretRoleForCommand(action));
    }

    [Fact]
    public void Unknown_or_client_selected_vault_role_is_denied()
    {
        Assert.Throws<TenantLifecycleDeniedException>(()=>
            VaultTenantOperatorSecrets.SecretRoleForCommand("../operator"));
        Assert.Throws<TenantLifecycleDeniedException>(()=>
            VaultTenantOperatorSecrets.SecretRoleForCommand("tenant-provisioning/provisioner"));
    }

}
