using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TsdServer.Core;
using TsdServer.Infrastructure;

namespace TsdServer.Host.Pages.Devices;

public sealed class IndexModel : PageModel
{
    readonly PortalDb _db;
    readonly ScanCoordinator _scan;
    readonly AgentDesk _desk;

    public IndexModel(PortalDb db, ScanCoordinator scan, AgentDesk desk)
    {
        _db = db;
        _scan = scan;
        _desk = desk;
    }

    public List<Device> Devices { get; private set; } = [];
    public bool Scanning => _scan.Running;
    public string? FinishedAt => _scan.FinishedAt is null ? null : Clock.When(_scan.FinishedAt);
    public string? ScanError => _scan.Error;

    public async Task OnGetAsync() =>
        Devices = await _db.Devices.OrderByDescending(x => x.LastSeenAt).ToListAsync();

    public IActionResult OnPostScan()
    {
        if (!User.IsInRole(Roles.Admin))
            return Forbid();
        _scan.Start(HttpContext.RequestServices);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (!User.IsInRole(Roles.Admin))
            return Forbid();
        var device = await _db.Devices.FirstOrDefaultAsync(x => x.Id == id);
        if (device is null)
            return RedirectToPage();
        var jobs = await _db.Jobs.Where(x => x.DeviceId == device.Id).ToListAsync();
        var logs = await _db.AgentLogs.Where(x => x.DeviceId == device.Id).ToListAsync();
        _db.Jobs.RemoveRange(jobs);
        _db.AgentLogs.RemoveRange(logs);
        _db.Devices.Remove(device);
        _db.Audit.Add(new AuditEntry
        {
            At = DateTime.UtcNow,
            User = User.Identity?.Name ?? "",
            Action = "device.delete",
            Detail = (device.Model ?? device.Id) + " " + device.Id
        });
        _desk.Close(device.Id);
        await _db.SaveChangesAsync();
        return RedirectToPage();
    }
}
