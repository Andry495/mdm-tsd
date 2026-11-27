using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TsdServer.Infrastructure;

namespace TsdServer.Host.Pages;

public sealed class IndexModel : PageModel
{
    readonly PortalDb _db;
    readonly PortalOptions _options;
    readonly SiteCertificate _certificate;

    public IndexModel(PortalDb db, PortalOptions options, SiteCertificate certificate)
    {
        _db = db;
        _options = options;
        _certificate = certificate;
    }

    public StoreItem? Agent { get; private set; }
    public List<StoreItem> Others { get; private set; } = [];
    public string Fingerprint => _certificate.Formatted;
    public string BaseUrl => _options.PublicBaseUrl;
    public string SiteName => _options.SiteName;

    public async Task OnGetAsync()
    {
        var packages = await _db.Packages.OrderBy(x => x.Title).ToListAsync();
        var versions = await _db.Versions.Where(x => x.Assignable).OrderByDescending(x => x.VersionCode).ToListAsync();
        var cards = new List<StoreItem>();
        foreach (var package in packages)
        {
            var version = versions.FirstOrDefault(x => x.PackageId == package.Id);
            if (version is null)
                continue;
            cards.Add(new StoreItem(package.Kind, package.Title, package.PackageName, package.Summary ?? "", version.Id, version.VersionName, version.VersionCode));
        }
        Agent = cards.FirstOrDefault(x => x.Kind == "agent");
        Others = cards.Where(x => x.Kind != "agent").ToList();
    }
}

public sealed record StoreItem(string Kind, string Title, string PackageName, string Summary, string VersionId, string VersionName, int VersionCode);
