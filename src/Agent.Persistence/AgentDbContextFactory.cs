using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agent.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to scaffold Npgsql migrations. The connection is never opened at design time.</summary>
internal sealed class AgentDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseNpgsql("Host=localhost;Database=agent_design;Username=design;Password=design")
            .Options;
        return new AgentDbContext(options);
    }
}
