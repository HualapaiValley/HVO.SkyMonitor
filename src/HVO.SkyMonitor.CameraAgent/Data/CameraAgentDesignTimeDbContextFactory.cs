using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HVO.SkyMonitor.CameraAgent.Data;

internal sealed class CameraAgentDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        var databasePath = Path.Combine(Directory.GetCurrentDirectory(), "cameraagent_identity_design.db");
        optionsBuilder.UseSqlite($"Data Source={databasePath}");
        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
