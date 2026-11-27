using Microsoft.EntityFrameworkCore;
using TsdServer.Core;

namespace TsdServer.Infrastructure;

public static class CatalogHistory
{
    public const int Limit = 7;

    public static async Task<int> TrimAsync(PortalDb db, string dataDir, CancellationToken cancellationToken = default)
    {
        var versions = await db.Versions.ToListAsync(cancellationToken);
        var assignments = await db.Assignments.ToListAsync(cancellationToken);
        var jobs = await db.Jobs.Where(x => x.FinishedAt == null).ToListAsync(cancellationToken);
        var removed = new List<PackageVersion>();
        foreach (var batch in versions.GroupBy(x => x.PackageId))
        {
            var ordered = batch.OrderByDescending(x => x.VersionCode).ThenByDescending(x => x.CreatedAt).ToList();
            if (ordered.Count <= Limit + 1)
                continue;
            var current = ordered[0];
            foreach (var stale in ordered.Skip(Limit + 1))
            {
                foreach (var assignment in assignments.Where(x => x.VersionId == stale.Id))
                    assignment.VersionId = current.Id;
                foreach (var job in jobs.Where(x => x.VersionId == stale.Id))
                {
                    job.State = JobStates.Failed;
                    job.Error = "версия удалена: в истории пакета хранятся семь прошлых";
                    job.FinishedAt = DateTime.UtcNow;
                }
                db.Versions.Remove(stale);
                removed.Add(stale);
            }
        }
        if (removed.Count == 0)
            return 0;
        db.Audit.Add(new AuditEntry
        {
            At = DateTime.UtcNow,
            User = "system",
            Action = "catalog.trim",
            Detail = "удалено версий " + removed.Count
        });
        await db.SaveChangesAsync(cancellationToken);
        foreach (var version in removed)
            DeleteStored(dataDir, version);
        return removed.Count;
    }

    static void DeleteStored(string dataDir, PackageVersion version)
    {
        try
        {
            var path = CatalogLayout.VersionPath(Path.Combine(dataDir, "catalog"), version.PackageId, version.Id, version.FileName);
            if (File.Exists(path))
                File.Delete(path);
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch
        {
        }
    }
}
