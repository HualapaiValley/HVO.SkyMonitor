using System.IO;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Data;

internal sealed class CameraAgentDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    private static readonly ServiceProvider IdentityModelServices = new ServiceCollection()
        .Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .BuildServiceProvider();

    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var databasePath = Path.Combine(Directory.GetCurrentDirectory(), "cameraagent_identity_design.db");
        return new ApplicationDbContext(CreateOptionsBuilder().UseSqlite($"Data Source={databasePath}").Options);
    }

    internal static DbContextOptionsBuilder<ApplicationDbContext> CreateOptionsBuilder()
        => new DbContextOptionsBuilder<ApplicationDbContext>().UseApplicationServiceProvider(IdentityModelServices);
}
