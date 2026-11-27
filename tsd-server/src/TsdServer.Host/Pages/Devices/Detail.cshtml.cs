using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TsdServer.Core;
using TsdServer.Infrastructure;

namespace TsdServer.Host.Pages.Devices;

public sealed class DetailModel : PageModel
{
    readonly PortalDb _db;
    public DetailModel(PortalDb db) => _db = db;

    public Device Device { get; private set; } = new();
    public List<DeviceGroup> Groups { get; private set; } = [];
    public string Suggested { get; private set; } = Families.Phone;
    public string? Error { get; private set; }
    public List<AgentLog> Logs { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string id)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(x => x.Id == id);
        if (device is null)
            return NotFound();
        Device = device;
        Suggested = device.Family ?? device.SuggestedFamily;
        Groups = await _db.Groups.OrderBy(x => x.Name).ToListAsync();
        Logs = await _db.AgentLogs.Where(x => x.DeviceId == device.Id).OrderByDescending(x => x.At).Take(40).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(string id, string family, string? groupId)
    {
        if (!User.IsInRole(Roles.Admin))
            return Forbid();
        var device = await _db.Devices.FirstOrDefaultAsync(x => x.Id == id);
        if (device is null)
            return NotFound();
        DeviceGroup? group = null;
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            group = await _db.Groups.FirstOrDefaultAsync(x => x.Id == groupId);
            if (group is null || group.Family != family)
            {
                Device = device;
                Groups = await _db.Groups.OrderBy(x => x.Name).ToListAsync();
                Suggested = family;
                Error = "Группа должна быть того же типа, что и устройство";
                return Page();
            }
        }
        device.Family = family;
        device.GroupId = group?.Id;
        device.State = DeviceStates.Managed;
        _db.Audit.Add(new AuditEntry
        {
            At = DateTime.UtcNow,
            User = User.Identity?.Name ?? "",
            Action = "device.confirm",
            Detail = device.Id
        });
        await _db.SaveChangesAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRejectAsync(string id)
    {
        if (!User.IsInRole(Roles.Admin))
            return Forbid();
        var device = await _db.Devices.FirstOrDefaultAsync(x => x.Id == id);
        if (device is null)
            return NotFound();
        device.State = DeviceStates.Rejected;
        device.GroupId = null;
        _db.Audit.Add(new AuditEntry
        {
            At = DateTime.UtcNow,
            User = User.Identity?.Name ?? "",
            Action = "device.reject",
            Detail = device.Id
        });
        await _db.SaveChangesAsync();
        return RedirectToPage(new { id });
    }
}
