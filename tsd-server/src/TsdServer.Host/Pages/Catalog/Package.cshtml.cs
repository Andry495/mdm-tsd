using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TsdServer.Core;
using TsdServer.Infrastructure;

namespace TsdServer.Host.Pages.Catalog;

[Authorize(Roles = Roles.Admin)]
public sealed class PackageModel : PageModel
{
    public const int HistoryLimit = CatalogHistory.Limit;
    readonly PortalDb _db;
    public PackageModel(PortalDb db) => _db = db;

    public CatalogPackage Package { get; private set; } = new();
    public VersionLine? Current { get; private set; }
    public List<VersionLine> History { get; private set; } = [];
    public bool HistoryTrimmed { get; private set; }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        var package = await _db.Packages.FirstOrDefaultAsync(x => x.Id == id);
        if (package is null)
            return NotFound();
        Package = package;
        var versions = await _db.Versions.Where(x => x.PackageId == package.Id).OrderByDescending(x => x.VersionCode).ToListAsync();
        var groups = await _db.Groups.ToListAsync();
        var assignments = await _db.Assignments.Where(x => x.PackageId == package.Id).ToListAsync();
        var lines = versions.Select(version => new VersionLine(
            version.Id,
            version.VersionName,
            version.VersionCode,
            string.Join(", ", assignments.Where(x => x.VersionId == version.Id)
                .Select(x => groups.FirstOrDefault(g => g.Id == x.GroupId)?.Name)
                .Where(x => x is not null)))).ToList();
        Current = lines.FirstOrDefault();
        var older = lines.Skip(1).ToList();
        HistoryTrimmed = older.Count > HistoryLimit;
        History = older.Take(HistoryLimit).ToList();
        return Page();
    }
}

public sealed record VersionLine(string Id, string VersionName, int VersionCode, string Groups);
