using Microsoft.EntityFrameworkCore;
using TsdServer.Core;

namespace TsdServer.Infrastructure;

public sealed class PortalDb : DbContext
{
    public PortalDb(DbContextOptions<PortalDb> options) : base(options) { }

    public DbSet<PortalUser> Users => Set<PortalUser>();
    public DbSet<DeviceGroup> Groups => Set<DeviceGroup>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<CatalogPackage> Packages => Set<CatalogPackage>();
    public DbSet<PackageVersion> Versions => Set<PackageVersion>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<AgentLog> AgentLogs => Set<AgentLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PortalUser>().HasIndex(x => x.Login).IsUnique();
        modelBuilder.Entity<Device>().HasIndex(x => x.State);
        modelBuilder.Entity<CatalogPackage>().HasIndex(x => x.PackageName).IsUnique();
        modelBuilder.Entity<PackageVersion>().HasIndex(x => new { x.PackageId, x.VersionCode }).IsUnique();
        modelBuilder.Entity<Assignment>().HasIndex(x => new { x.GroupId, x.PackageId }).IsUnique();
        modelBuilder.Entity<Job>().HasIndex(x => new { x.DeviceId, x.State });
    }
}
