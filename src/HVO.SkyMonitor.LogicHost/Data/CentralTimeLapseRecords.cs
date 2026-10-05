using HVO.SkyMonitor.LogicHost.Services.TimeLapses;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralTimeLapseJob
{
    public Guid Id { get; set; }
    public Guid DevicePublicId { get; set; }
    public Guid ObservatoryId { get; set; }
    public DateOnly ReportDate { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public bool IsDaily { get; set; }
    public string? DiscoveryIdentity { get; set; }
    public string RequestJson { get; set; } = string.Empty;
    public string RequestSha256 { get; set; } = string.Empty;
    public CentralTimeLapseState State { get; set; }
    public string? ReasonCode { get; set; }
    public string? ExclusionsJson { get; set; }
    public string? ExclusionsSha256 { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<CentralTimeLapseInput> Inputs { get; } = [];
    public ICollection<CentralTimeLapseDependency> Dependencies { get; } = [];
}

internal sealed class CentralTimeLapseInput
{
    public Guid JobId { get; set; }
    public CentralTimeLapseJob? Job { get; set; }
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
}

internal sealed class CentralTimeLapseDependency
{
    public Guid JobId { get; set; }
    public CentralTimeLapseJob? Job { get; set; }
    public Guid HourlyJobId { get; set; }
    public CentralTimeLapseJob? HourlyJob { get; set; }
}

internal sealed class CentralTimeLapseVideo
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid DevicePublicId { get; set; }
    public Guid ObservatoryId { get; set; }
    public DateOnly ReportDate { get; set; }
    public bool IsDaily { get; set; }
    public bool IsGapFiller { get; set; }
    public string ProductJson { get; set; } = string.Empty;
    public string ProductSha256 { get; set; } = string.Empty;
    public long PayloadBytes { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

internal static class CentralTimeLapseConfiguration
{
    internal static void Configure(ModelBuilder builder)
    {
        var job = builder.Entity<CentralTimeLapseJob>();
        job.ToTable("CentralTimeLapseJobs", table => table.HasTrigger("TR_CentralTimeLapseJobs_Identity"));
        job.HasKey(static item => item.Id);
        job.Property(static item => item.State).HasConversion<string>().HasMaxLength(24);
        job.Property(static item => item.ReasonCode).HasMaxLength(128);
        job.Property(static item => item.RequestSha256).HasMaxLength(64).IsFixedLength();
        job.Property(static item => item.DiscoveryIdentity).HasMaxLength(64).IsFixedLength();
        job.Property(static item => item.ExclusionsSha256).HasMaxLength(64).IsFixedLength();
        job.Property(static item => item.RowVersion).IsRowVersion();
        job.HasIndex(static item => new { item.State, item.IsDaily, item.CreatedUtc });
        job.HasIndex(static item => new { item.DevicePublicId, item.ReportDate, item.CreatedUtc });
        job.HasIndex(static item => new { item.DevicePublicId, item.ReportDate, item.DiscoveryIdentity, item.CreatedUtc });
        var input = builder.Entity<CentralTimeLapseInput>();
        input.ToTable("CentralTimeLapseInputs", table => table.HasTrigger("TR_CentralTimeLapseInputs_Immutable"));
        input.HasKey(static item => new { item.JobId, item.CentralArtifactId });
        input.HasOne(static item => item.Job).WithMany(static item => item.Inputs).HasForeignKey(static item => item.JobId).OnDelete(DeleteBehavior.Restrict);
        input.HasOne(static item => item.Artifact).WithMany().HasForeignKey(static item => item.CentralArtifactId).OnDelete(DeleteBehavior.Restrict);
        var dependency = builder.Entity<CentralTimeLapseDependency>();
        dependency.ToTable("CentralTimeLapseDependencies", table => table.HasTrigger("TR_CentralTimeLapseDependencies_Immutable"));
        dependency.HasKey(static item => new { item.JobId, item.HourlyJobId });
        dependency.HasOne(static item => item.Job).WithMany(static item => item.Dependencies).HasForeignKey(static item => item.JobId).OnDelete(DeleteBehavior.Restrict);
        dependency.HasOne(static item => item.HourlyJob).WithMany().HasForeignKey(static item => item.HourlyJobId).OnDelete(DeleteBehavior.Restrict);
        var video = builder.Entity<CentralTimeLapseVideo>();
        video.ToTable("CentralTimeLapseVideos", table => table.HasTrigger("TR_CentralTimeLapseVideos_Immutable"));
        video.HasKey(static item => item.Id);
        video.HasOne<CentralTimeLapseJob>().WithMany().HasForeignKey(static item => item.JobId).OnDelete(DeleteBehavior.Restrict);
        video.HasIndex(static item => item.JobId).IsUnique();
        video.HasIndex(static item => new { item.DevicePublicId, item.ReportDate, item.CreatedUtc });
        video.Property(static item => item.ProductSha256).HasMaxLength(64).IsFixedLength();
    }
}
