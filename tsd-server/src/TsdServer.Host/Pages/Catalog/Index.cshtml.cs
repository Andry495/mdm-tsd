using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TsdServer.Core;
using TsdServer.Infrastructure;

namespace TsdServer.Host.Pages.Catalog;

[Authorize(Roles = Roles.Admin)]
public sealed class IndexModel : PageModel
{
    readonly PortalDb _db;
    readonly PortalOptions _options;
    public IndexModel(PortalDb db, PortalOptions options)
    {
        _db = db;
        _options = options;
    }

    public List<DeviceGroup> Groups { get; private set; } = [];
    public List<VersionChoice> Versions { get; private set; } = [];
    public List<CatalogRow> Rows { get; private set; } = [];
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostUploadAsync(string? title, IFormFile? apk)
    {
        if (apk is null || apk.Length == 0)
        {
            Error = "Выберите APK";
            await LoadAsync();
            return Page();
        }
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".apk");
        try
        {
            await using (var stream = System.IO.File.Create(temp))
                await apk.CopyToAsync(stream);
            var info = ApkInspector.Read(temp);
            var package = await _db.Packages.FirstOrDefaultAsync(x => x.PackageName == info.PackageName);
            if (package is null)
            {
                package = new CatalogPackage
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Kind = info.PackageName == "ru.bytplast.tsdagent" ? PackageKinds.Agent : PackageKinds.Apk,
                    Title = string.IsNullOrWhiteSpace(title) ? info.PackageName : title.Trim(),
                    PackageName = info.PackageName,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Packages.Add(package);
            }
            else if (!string.IsNullOrWhiteSpace(title))
            {
                package.Title = title.Trim();
            }
            var sha = ApkInspector.Sha256(temp);
            var fileName = Path.GetFileName(apk.FileName);
            var previous = await _db.Versions.FirstOrDefaultAsync(x => x.PackageId == package.Id && x.VersionCode == info.VersionCode);
            var versionId = previous?.Id ?? Guid.NewGuid().ToString("D");
            var version = previous ?? new PackageVersion { Id = versionId, PackageId = package.Id };
            version.VersionCode = info.VersionCode;
            version.VersionName = info.VersionName;
            version.Sha256 = sha;
            version.FileName = fileName;
            version.Size = apk.Length;
            version.Assignable = true;
            version.CreatedAt = DateTime.UtcNow;
            var dest = CatalogLayout.VersionPath(Path.Combine(_options.DataDir, "catalog"), package.Id, version.Id, version.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            System.IO.File.Copy(temp, dest, true);
            if (previous is null)
                _db.Versions.Add(version);
            _db.Audit.Add(new AuditEntry
            {
                At = DateTime.UtcNow,
                User = User.Identity?.Name ?? "",
                Action = "catalog.upload",
                Detail = info.PackageName + " " + info.VersionName
            });
            await _db.SaveChangesAsync();
            await CatalogHistory.TrimAsync(_db, _options.DataDir);
            Message = "Пакет добавлен: " + package.Title + " " + info.VersionName;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            if (System.IO.File.Exists(temp))
                System.IO.File.Delete(temp);
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostGroupAsync(string name, string family)
    {
        if (!Families.All.Contains(family) || string.IsNullOrWhiteSpace(name))
        {
            Error = "Укажите название и тип";
            await LoadAsync();
            return Page();
        }
        _db.Groups.Add(new DeviceGroup
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = name.Trim(),
            Family = family,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        Message = "Группа создана";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(string groupId, string versionId)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(x => x.Id == groupId);
        var version = await _db.Versions.FirstOrDefaultAsync(x => x.Id == versionId);
        if (group is null || version is null)
        {
            Error = "Группа или версия не найдены";
            await LoadAsync();
            return Page();
        }
        var current = await _db.Assignments.FirstOrDefaultAsync(x => x.GroupId == group.Id && x.PackageId == version.PackageId);
        if (current is null)
        {
            _db.Assignments.Add(new Assignment
            {
                Id = Guid.NewGuid().ToString("D"),
                GroupId = group.Id,
                PackageId = version.PackageId,
                VersionId = version.Id,
                Required = true
            });
        }
        else
        {
            current.VersionId = version.Id;
        }
        _db.Audit.Add(new AuditEntry
        {
            At = DateTime.UtcNow,
            User = User.Identity?.Name ?? "",
            Action = "catalog.assign",
            Detail = group.Name + " " + version.VersionName
        });
        await _db.SaveChangesAsync();
        Message = "Версия назначена группе";
        await LoadAsync();
        return Page();
    }

    async Task LoadAsync()
    {
        Groups = await _db.Groups.OrderBy(x => x.Name).ToListAsync();
        var packages = await _db.Packages.ToListAsync();
        var versions = await _db.Versions.OrderByDescending(x => x.VersionCode).ToListAsync();
        var assignments = await _db.Assignments.ToListAsync();
        var current = packages
            .Select(package => versions.Where(x => x.PackageId == package.Id).OrderByDescending(x => x.VersionCode).FirstOrDefault())
            .Where(x => x is not null)
            .Cast<PackageVersion>()
            .OrderBy(x => packages.First(p => p.Id == x.PackageId).Title)
            .ToList();
        Versions = current.Select(version =>
        {
            var package = packages.First(x => x.Id == version.PackageId);
            return new VersionChoice(version.Id, package.Title + " " + version.VersionName);
        }).ToList();
        Rows = current.Select(version =>
        {
            var package = packages.First(x => x.Id == version.PackageId);
            var names = assignments.Where(x => x.PackageId == package.Id)
                .Select(x => Groups.FirstOrDefault(g => g.Id == x.GroupId)?.Name)
                .Where(x => x is not null)
                .Distinct();
            return new CatalogRow(package.Id, package.Title, package.PackageName, version.VersionName, version.VersionCode, string.Join(", ", names));
        }).ToList();
    }
}

public sealed record VersionChoice(string Id, string Label);
public sealed record CatalogRow(string PackageId, string Title, string PackageName, string VersionName, int VersionCode, string Groups);
