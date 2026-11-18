using System.Data;
using System.Security.Cryptography;
using FmcgDms.Preprod.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Tenancy.SqlServer.Tests;

// GitHub-hosted Windows LocalDB ONLY. All names carry a random CI nonce.
// This helper NEVER accepts an arbitrary delete target from a request/operator.
internal sealed class B4SqlFixture : IAsyncDisposable
{
    private readonly string server;
    private readonly List<string> databases=[];
    private readonly List<string> logins=[];
    public string Master => Connection("master");
    public string Control {get;}
    public string CurrentLogin {get;private set;}="";
    public string Requester {get;}
    public string Checker {get;}
    public string Verifier {get;}
    public string Security {get;}

    private B4SqlFixture(string sqlServer, string nonce)
    {
        server=sqlServer;
        Control=Connection("FmcgB4Control_"+nonce);
        Requester="b4req_"+nonce;
        Checker="b4app_"+nonce;
        Verifier="b4ver_"+nonce;
        Security="b4sec_"+nonce;
    }

    public static async Task<B4SqlFixture> CreateAsync(CancellationToken ct)
    {
        var server=Environment.GetEnvironmentVariable("APP02B4_SQL_SERVER");
        if(string.IsNullOrWhiteSpace(server) || !server.Contains("(localdb)",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("B4 tests require disposable CI LocalDB, never a real PREPROD server");
        var nonce=Guid.NewGuid().ToString("N")[..12];
        var f=new B4SqlFixture(server,nonce);
        await f.CreateDbAsync(new SqlConnectionStringBuilder(f.Control).InitialCatalog,ct);
        f.CurrentLogin=(string)(await f.ScalarAsync(f.Control,"SELECT SUSER_SNAME()",ct))!;
        await f.ExecAsync(f.Control,ReadRegistryMigration(),ct);
        await f.ExecAsync(f.Control,ReadRegistryMigration(),ct);
        await f.AddActorAsync(f.Requester,request:true,approve:true,ct:ct);
        await f.AddActorAsync(f.Checker,approve:true,ct:ct);
        await f.AddActorAsync(f.Verifier,verify:true,ct:ct);
        await f.AddActorAsync(f.Security,suspend:true,ct:ct);
        await f.ExecAsync(f.Control,
            @"INSERT dbo.PreprodTenantOperators(LoginName,Enabled,CanProvision)
              VALUES(@login,1,1)",ct,new SqlParameter("@login",f.CurrentLogin));
        return f;
    }

    public string Connection(string db) =>
        new SqlConnectionStringBuilder
        {
            DataSource=server,InitialCatalog=db,IntegratedSecurity=true,
            Encrypt=SqlConnectionEncryptOption.Optional,TrustServerCertificate=true,
            ConnectTimeout=20,Pooling=false
        }.ConnectionString;

    public async Task CreateDbAsync(string name,CancellationToken ct)
    {
        if(!name.StartsWith("FmcgB4",StringComparison.Ordinal) &&
            !name.StartsWith("FmcgPpT_",StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid CI scratch database prefix");
        await ExecAsync(Master,$"CREATE DATABASE [{name}]",ct);
        databases.Add(name);
    }

    public void TrackDb(string name)
    {
        if(!name.StartsWith("FmcgPpT_",StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid tracked tenant database");
        if(!databases.Contains(name,StringComparer.OrdinalIgnoreCase)) databases.Add(name);
    }

    public async Task AddActorAsync(string name,bool request=false,bool approve=false,
        bool verify=false,bool suspend=false,CancellationToken ct=default)
    {
        if(!name.StartsWith("b4",StringComparison.Ordinal)) throw new InvalidOperationException();
        var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(20))+"aA1!";
        await ExecAsync(Master,$"CREATE LOGIN [{name}] WITH PASSWORD=N'{password}',CHECK_POLICY=OFF",ct);
        logins.Add(name);
        await ExecAsync(Control,$"CREATE USER [{name}] FOR LOGIN [{name}]; GRANT EXECUTE ON SCHEMA::dbo TO [{name}];",ct);
        await ExecAsync(Control,@"INSERT dbo.PreprodTenantOperators
            (LoginName,Enabled,CanRequest,CanApprove,CanVerify,CanSuspend)
            VALUES(@name,1,@request,@approve,@verify,@suspend)",ct,
            new SqlParameter("@name",name),
            new SqlParameter("@request",request),
            new SqlParameter("@approve",approve),
            new SqlParameter("@verify",verify),
            new SqlParameter("@suspend",suspend));
    }

    public async Task<string> CreateBareLoginAsync(string prefix,CancellationToken ct)
    {
        if(!prefix.StartsWith("b4",StringComparison.Ordinal))
            throw new InvalidOperationException("Unapproved CI principal prefix");
        var login=prefix+"_"+Guid.NewGuid().ToString("N")[..12];
        var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(20))+"aA1!";
        await ExecAsync(Master,$"CREATE LOGIN [{login}] WITH PASSWORD=N'{password}',CHECK_POLICY=OFF",ct);
        logins.Add(login);
        return login;
    }

    public async Task<string> CreateDdlLoginAsync(CancellationToken ct)
    {
        var login=await CreateBareLoginAsync("b4ddl",ct);
        await ExecAsync(Master,$"GRANT CREATE ANY DATABASE TO [{login}]",ct);
        return login;
    }

    public async Task<object?> ScalarAsLoginAsync(string login,string sql,CancellationToken ct)
    {
        await using var db=new SqlConnection(Master);
        await db.OpenAsync(ct);
        await ImpersonateAsync(db,login,ct);
        await using var query=new SqlCommand(sql,db);
        return await query.ExecuteScalarAsync(ct);
    }

    public static async Task ImpersonateAsync(SqlConnection connection,string login,CancellationToken ct)
    {
        // Only CI-generated SQL Server logins are passed here; prove real SQL
        // permission checking under EXECUTE AS instead of mock SqlException.
        if(!login.StartsWith("b4",StringComparison.Ordinal))
            throw new InvalidOperationException("CI impersonation login not allowed");
        await using var command=new SqlCommand($"EXECUTE AS LOGIN=N'{login}'",connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task RunAsAsync(string login,string invocation,CancellationToken ct,
        params SqlParameter[] parameters)
    {
        if(!logins.Contains(login)) throw new InvalidOperationException("Unknown CI actor");
        var sql=$"EXECUTE AS LOGIN=N'{login}'; BEGIN TRY {invocation}; REVERT; END TRY BEGIN CATCH REVERT; THROW; END CATCH;";
        return ExecAsync(Control,sql,ct,parameters);
    }

    public async Task ExecAsync(string conn,string sql,CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var c=new SqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd=new SqlCommand(sql,c) {CommandTimeout=120};
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<object?> ScalarAsync(string conn,string sql,CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var c=new SqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd=new SqlCommand(sql,c) {CommandTimeout=90};
        cmd.Parameters.AddRange(parameters);
        return await cmd.ExecuteScalarAsync(ct);
    }

    public static string ReadRegistryMigration()
    {
        var a=typeof(TenantRegistrySql).Assembly;
        var name=a.GetManifestResourceNames().Single(x=>
            x.EndsWith("20261118_001_registry_control_plane.sql",StringComparison.Ordinal));
        using var reader=new StreamReader(a.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        foreach(var db in databases.AsEnumerable().Reverse())
        {
            try
            {
                await ExecAsync(Master,$@"IF DB_ID(N'{db}') IS NOT NULL
                    BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{db}]; END",CancellationToken.None);
            }
            catch(SqlException){ /* isolated ephemeral runner, never production */ }
        }
        foreach(var login in logins)
        {
            try {await ExecAsync(Master,$"DROP LOGIN [{login}]",CancellationToken.None);}
            catch(SqlException){ /* ephemeral runner cleanup */ }
        }
    }
}
