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
    public void Finalizer_admission_requires_its_own_pinned_sql_identity_and_master_target()
    {
        const string registry="Server=sql.internal;Database=control;Encrypt=True;TrustServerCertificate=False;User Id=b4final;Password=example-only";
        const string master="Server=sql.internal;Database=master;Encrypt=True;TrustServerCertificate=False;User Id=b4final;Password=example-only";
        TenantOperatorAdmission.ValidateFinalizer("Preprod","https://vault.internal.example",registry,master);
        Assert.Throws<TenantLifecycleDeniedException>(()=>
            TenantOperatorAdmission.ValidateFinalizer("Production","https://vault.internal.example",registry,master));
        Assert.Throws<TenantLifecycleDeniedException>(()=>
            TenantOperatorAdmission.ValidateFinalizer("Preprod","https://vault.internal.example",registry,
                master.Replace("b4final","other-sql-login")));
        Assert.Throws<TenantLifecycleDeniedException>(()=>
            TenantOperatorAdmission.ValidateFinalizer("Preprod","https://vault.internal.example",registry,
                master.Replace("Server=sql.internal","Server=peer.internal")));
    }

    [Fact]
    public void Non_active_registry_states_are_always_non_routable()
    {
        foreach(var state in new[]{"REQUESTED","APPROVED","PROVISIONING","FAILED_RETRYABLE",
            "VERIFYING","FINALIZING","SUSPENDED","REACTIVATION_VERIFYING","DECOMMISSIONING",
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
                .VerifyAsync(Guid.NewGuid(),
                    new TenantVerifierProbeSecrets("","approved-owner","ddl-provisioner"),
                    TestContext.Current.CancellationToken));
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
    [InlineData("finalize","finalizer")]
    [InlineData("verify","verifier")]
    [InlineData("suspend","security")]
    [InlineData("revoke","security")]
    [InlineData("complete-decommission","verifier")]
    public void Vault_operator_roles_are_fixed_and_separated(string action,string role)
    {
        Assert.Equal(role,VaultTenantOperatorSecrets.SecretRoleForCommand(action));
    }

    [Fact]
    public async Task Finalizer_vault_uses_only_exact_tenant_path_and_never_shared_role()
    {
        var tenant=Guid.Parse("20ad96e7-d561-4d5d-9f79-3b8b1b1ea402");
        var file=Path.GetTempFileName();
        var handler=new FinalizerVaultStub();
        try
        {
            await File.WriteAllTextAsync(file,"ci-only-token-material-123456");
            var vault=new VaultTenantOperatorSecrets("https://vault.internal.example",file,handler);
            var result=await vault.ReadFinalizerAsync(tenant,TestContext.Current.CancellationToken);
            Assert.Equal("Server=sql.internal;Database=control",result.RegistryConnection);
            Assert.Equal("Server=sql.internal;Database=master",result.MasterConnection);
            Assert.Equal("/v1/preprod/data/tenant-provisioning/finalizer/"+tenant.ToString("N"),
                handler.RequestedPath);
            await Assert.ThrowsAsync<TenantLifecycleDeniedException>(()=>
                vault.ReadFinalizerAsync(Guid.Empty,TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<TenantLifecycleDeniedException>(()=>
                vault.ReadOperatorAsync("finalize",TestContext.Current.CancellationToken));
        }
        finally {File.Delete(file);}
    }

    private sealed class FinalizerVaultStub : HttpMessageHandler
    {
        public string? RequestedPath {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken cancellationToken)
        {
            RequestedPath=request.RequestUri?.AbsolutePath;
            const string body="""
            {"data":{"metadata":{"version":1},"data":{
              "registryConnectionString":"Server=sql.internal;Database=control",
              "masterConnectionString":"Server=sql.internal;Database=master"}}}
            """;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content=new StringContent(body)
            });
        }
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
