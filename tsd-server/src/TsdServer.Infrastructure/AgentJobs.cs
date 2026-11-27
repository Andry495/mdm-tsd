using Microsoft.EntityFrameworkCore;
using TsdServer.Core;

namespace TsdServer.Infrastructure;

public sealed record InstallOffer(string JobId, string PackageName, int VersionCode, string Sha256, string Url);

public static class AgentJobs
{
    public const string AgentPackage = "ru.bytplast.tsdagent";

    public static async Task<InstallOffer?> NextAsync(PortalDb db, Device device, string portalBase, CancellationToken cancellationToken)
    {
        await RetireStaleAgentJobsAsync(db, device, cancellationToken);
        var agent = await OfferAgentUpdateAsync(db, device, portalBase, cancellationToken);
        if (agent is not null)
            return agent;
        var queued = await db.Jobs.Where(x => x.DeviceId == device.Id && x.State == JobStates.Queued && x.Kind == "install")
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (queued is null || string.IsNullOrWhiteSpace(queued.VersionId))
            return null;
        return await OfferAsync(db, queued, portalBase, cancellationToken);
    }

    static async Task RetireStaleAgentJobsAsync(PortalDb db, Device device, CancellationToken cancellationToken)
    {
        var package = await db.Packages.FirstOrDefaultAsync(x => x.PackageName == AgentPackage, cancellationToken);
        if (package is null)
            return;
        var current = device.AgentCode ?? 0;
        var queued = await db.Jobs.Where(x => x.DeviceId == device.Id && x.State == JobStates.Queued && x.Kind == "install").ToListAsync(cancellationToken);
        var changed = false;
        foreach (var job in queued)
        {
            if (string.IsNullOrWhiteSpace(job.VersionId))
                continue;
            var version = await db.Versions.FirstOrDefaultAsync(x => x.Id == job.VersionId, cancellationToken);
            if (version is null || version.PackageId != package.Id || version.VersionCode > current)
                continue;
            job.State = JobStates.Failed;
            job.Error = "телефон уже на версии " + current;
            job.FinishedAt = DateTime.UtcNow;
            changed = true;
        }
        if (changed)
            await db.SaveChangesAsync(cancellationToken);
    }

    static async Task<InstallOffer?> OfferAgentUpdateAsync(PortalDb db, Device device, string portalBase, CancellationToken cancellationToken)
    {
        var package = await db.Packages.FirstOrDefaultAsync(x => x.PackageName == AgentPackage, cancellationToken);
        if (package is null)
            return null;
        var latest = await db.Versions.Where(x => x.PackageId == package.Id && x.Assignable)
            .OrderByDescending(x => x.VersionCode)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null || (device.AgentCode ?? 0) >= latest.VersionCode)
            return null;
        var open = await db.Jobs.Where(x => x.DeviceId == device.Id && x.VersionId == latest.Id && x.Kind == "install" && x.FinishedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (open is not null)
            return await OfferAsync(db, open, portalBase, cancellationToken);
        var recent = await db.Jobs.Where(x => x.DeviceId == device.Id && x.VersionId == latest.Id && x.Kind == "install" && x.FinishedAt != null)
            .OrderByDescending(x => x.FinishedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (recent?.FinishedAt > DateTime.UtcNow.AddSeconds(-20))
            return null;
        open = new Job
        {
            Id = Guid.NewGuid().ToString("D"),
            DeviceId = device.Id,
            Kind = "install",
            VersionId = latest.Id,
            State = JobStates.Queued,
            CreatedAt = DateTime.UtcNow
        };
        db.Jobs.Add(open);
        await db.SaveChangesAsync(cancellationToken);
        return await OfferAsync(db, open, portalBase, cancellationToken);
    }

    static async Task<InstallOffer?> OfferAsync(PortalDb db, Job job, string portalBase, CancellationToken cancellationToken)
    {
        var version = string.IsNullOrWhiteSpace(job.VersionId)
            ? null
            : await db.Versions.FirstOrDefaultAsync(x => x.Id == job.VersionId, cancellationToken);
        var package = version is null
            ? null
            : await db.Packages.FirstOrDefaultAsync(x => x.Id == version.PackageId, cancellationToken);
        if (version is null || package is null)
            return null;
        return new InstallOffer(
            job.Id,
            package.PackageName,
            version.VersionCode,
            version.Sha256,
            portalBase.TrimEnd('/') + "/api/agent/files/versions/" + version.Id);
    }
}
