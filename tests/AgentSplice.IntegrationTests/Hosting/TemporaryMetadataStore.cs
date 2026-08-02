using AgentSplice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// A SQLite metadata store in a unique temporary file, deleted when the test ends.
/// </summary>
/// <remarks>
/// A file rather than <c>:memory:</c>. The in-memory provider drops the database as soon as the last
/// connection closes, and the writer opens a fresh context per batch, so the schema the initializer
/// created would be gone before the first row was written — the store under test would not be the
/// store the product uses.
///
/// One file per instance, so test classes running in parallel cannot lock each other out.
/// </remarks>
internal sealed class TemporaryMetadataStore : IDisposable
{
    private readonly string path;

    internal TemporaryMetadataStore()
    {
        path = Path.Combine(
            Path.GetTempPath(),
            "agentsplice-tests",
            Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture) + ".db");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>The connection string a host should be configured with.</summary>
    internal string ConnectionString => "Data Source=" + path;

    /// <summary>Applies the store's settings to a host configuration dictionary.</summary>
    internal void ApplyTo(Dictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings["agentsplice:persistence:mode"] = "Sqlite";
        settings["agentsplice:persistence:connectionString"] = ConnectionString;
    }

    /// <summary>Opens a context for reading what the gateway wrote.</summary>
    internal AgentSpliceDbContext OpenContext() => new(Options());

    /// <summary>A factory for the services that take one, outside a host.</summary>
    internal IDbContextFactory<AgentSpliceDbContext> ContextFactory() => new Factory(Options());

    private DbContextOptions<AgentSpliceDbContext> Options() =>
        new DbContextOptionsBuilder<AgentSpliceDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

    private sealed class Factory : IDbContextFactory<AgentSpliceDbContext>
    {
        private readonly DbContextOptions<AgentSpliceDbContext> options;

        internal Factory(DbContextOptions<AgentSpliceDbContext> options) => this.options = options;

        public AgentSpliceDbContext CreateDbContext() => new(options);
    }

    public void Dispose()
    {
        // The provider pools connections, and on Windows an open handle keeps the file locked.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file is not worth failing a passing test over.
        }
    }
}
