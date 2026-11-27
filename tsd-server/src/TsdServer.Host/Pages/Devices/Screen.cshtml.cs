using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TsdServer.Infrastructure;

namespace TsdServer.Host.Pages.Devices;

public sealed class ScreenModel : PageModel
{
    readonly PortalDb _db;
    public ScreenModel(PortalDb db) => _db = db;
    public string DeviceId { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string id)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(x => x.Id == id);
        if (device is null)
            return NotFound();
        DeviceId = device.Id;
        return Page();
    }
}
