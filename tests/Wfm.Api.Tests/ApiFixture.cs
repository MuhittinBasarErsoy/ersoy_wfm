using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Wfm.Application.Contracts;
using Wfm.Client;

namespace Wfm.Api.Tests;

/// <summary>Her test sınıfı için demo verili, izole bir LocalDB veritabanı üzerinde API.</summary>
public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbName = $"WfmTest_{Guid.NewGuid():N}";
    private string ConnectionString => $"Server=(localdb)\\MSSQLLocalDB;Database={_dbName};Trusted_Connection=True;TrustServerCertificate=True";
    private readonly string _uploads = Path.Combine(Path.GetTempPath(), "wfm-test-uploads", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Wfm", ConnectionString);
        builder.UseSetting("Seed:DemoData", "true");
        builder.UseSetting("Storage:UploadPath", _uploads);
    }

    /// <summary>Demo kullanıcıyla giriş yapmış bir API istemcisi.</summary>
    public async Task<WfmApiClient> LoginAs(string email)
    {
        var options = new WfmClientOptions { ApiBaseUrl = Server.BaseAddress.ToString() };
        var session = new AuthSession(new MemoryTokenStore(), options);
        var http = new HttpClient(new AuthHeaderHandler(session) { InnerHandler = Server.CreateHandler() }) { BaseAddress = Server.BaseAddress };
        var client = new WfmApiClient(http, options);
        await session.SetAsync(await client.LoginAsync(new LoginRequest(email, "Demo123!")));
        return client;
    }

    public Task InitializeAsync()
    {
        _ = Server; // sunucuyu başlat → migration + seed
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection("Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID('{_dbName}') IS NOT NULL BEGIN ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}]; END";
        await cmd.ExecuteNonQueryAsync();
        if (Directory.Exists(_uploads)) Directory.Delete(_uploads, true);
    }

    private sealed class MemoryTokenStore : ITokenStore
    {
        private AuthResponse? _auth;
        public Task<AuthResponse?> GetAsync() => Task.FromResult(_auth);
        public Task SetAsync(AuthResponse? auth) { _auth = auth; return Task.CompletedTask; }
    }
}
