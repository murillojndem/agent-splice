using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Builds a context for <c>dotnet ef</c> alone.
/// </summary>
/// <remarks>
/// Migration scaffolding needs a context instance before a host exists. Without this, the tooling
/// would start the API's composition root to find one, which means binding configuration, validating
/// it, and resolving runtimes — none of which a developer adding a column should have to have working.
///
/// The connection string is a scaffolding placeholder and never opens a connection: generating a
/// migration reads the model, not the database. The store an actual deployment uses comes from
/// <c>agentsplice:persistence:connectionString</c>.
/// </remarks>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentSpliceDbContext>
{
    public AgentSpliceDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AgentSpliceDbContext>()
            .UseSqlite("Data Source=agentsplice-design-time.db")
            .Options);
}
