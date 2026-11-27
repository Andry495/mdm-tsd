using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using TsdServer.Core;
using TsdServer.Host;
using TsdServer.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var portal = builder.Configuration.GetSection("Portal").Get<PortalOptions>() ?? new PortalOptions();
if (string.IsNullOrWhiteSpace(portal.AdminPassword))
    portal.AdminPassword = builder.Configuration["Portal:AdminPassword"] ?? "";

Directory.CreateDirectory(Path.Combine(portal.DataDir, "db"));
Directory.CreateDirectory(Path.Combine(portal.DataDir, "catalog"));
Directory.CreateDirectory(Path.Combine(portal.DataDir, "secrets"));

builder.Services.AddSingleton(portal);
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddSingleton<SiteCertificate>();
builder.Services.AddSingleton<AgentDesk>();
builder.Services.AddScoped<DiscoveryService>();
builder.Services.AddScoped<AgentChannel>();
builder.Services.AddDbContext<PortalDb>(options =>
    options.UseSqlite($"Data Source={Path.Combine(portal.DataDir, "db", "portal.db")}"));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(portal.DataDir, "secrets")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Login");
});
builder.Services.AddHostedService<ReconcileWorker>();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 256 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PortalDb>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    foreach (var column in new[] { "AndroidId TEXT", "UpdatePhase TEXT", "ScreenPhase TEXT", "Name TEXT" })
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Devices ADD COLUMN " + column);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
    }
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS AgentLogs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            DeviceId TEXT NOT NULL,
            At TEXT NOT NULL,
            Area TEXT NOT NULL,
            Text TEXT NOT NULL
        );
        """);
    await PortalSeed.EnsureAsync(db, portal);
}

app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapPost("/api/agent/announce", async (Announce body, HttpContext http, DiscoveryService discovery, PortalDb db) =>
{
    try
    {
        var state = await discovery.RegisterAsync(db, body, ClientAddress(http), http.RequestAborted);
        return Results.Ok(new { ok = true, state });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
    }
});

app.MapPost("/api/agent/wait", async (AgentCall body, HttpContext http, PortalDb db, AgentDesk desk, CancellationToken cancellationToken) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == body.DeviceId, cancellationToken);
    if (device is null || !AgentDesk.Signed(device, body.Nonce, body.Signature))
        return Results.Json(new { ok = false, error = "подпись устройства не сошлась" }, statusCode: StatusCodes.Status401Unauthorized);
    if (body.AgentCode > 0)
        device.AgentCode = body.AgentCode;
    device.LastSeenAt = DateTime.UtcNow;
    var address = ClientAddress(http);
    if (!string.IsNullOrWhiteSpace(address))
        device.LastAddress = address;
    if (!string.IsNullOrWhiteSpace(body.AndroidId))
        device.AndroidId = body.AndroidId;
    if (!string.IsNullOrWhiteSpace(body.Name))
        device.Name = body.Name.Trim();
    if (!string.IsNullOrWhiteSpace(body.UpdatePhase))
        device.UpdatePhase = body.UpdatePhase;
    if (!string.IsNullOrWhiteSpace(body.ScreenPhase))
        device.ScreenPhase = body.ScreenPhase;
    if (body.Notes is not null)
    {
        foreach (var note in body.Notes.Where(x => !string.IsNullOrWhiteSpace(x.Text)).Take(40))
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(note.At).UtcDateTime;
            var exists = await db.AgentLogs.AnyAsync(x => x.DeviceId == device.Id && x.Area == note.Area && x.Text == note.Text && x.At == at, cancellationToken);
            if (exists)
                continue;
            db.AgentLogs.Add(new AgentLog { DeviceId = device.Id, At = at, Area = string.IsNullOrWhiteSpace(note.Area) ? "agent" : note.Area, Text = note.Text });
        }
    }
    await DiscoveryService.CollapseTwinsAsync(db, device, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    if (device.State != DeviceStates.Managed)
        return Results.Ok(new { ok = true, state = device.State });
    var pending = await AgentJobs.NextAsync(db, device, portal.PublicBaseUrl, cancellationToken);
    var session = desk.Session(device.Id);
    if (pending is not null && !desk.Claim(pending.JobId))
        pending = null;
    if (pending is null && session is null)
    {
        await desk.WaitAsync(device.Id, cancellationToken);
        pending = await AgentJobs.NextAsync(db, device, portal.PublicBaseUrl, cancellationToken);
        session = desk.Session(device.Id);
        if (pending is not null && !desk.Claim(pending.JobId))
            pending = null;
    }
    if (pending is not null)
    {
        var offered = await db.Jobs.FirstAsync(x => x.Id == pending.JobId, cancellationToken);
        if (offered.CreatedAt > DateTime.UtcNow.AddSeconds(-5))
        {
            db.AgentLogs.Add(new AgentLog
            {
                DeviceId = device.Id,
                At = DateTime.UtcNow,
                Area = "portal",
                Text = "отдано задание установки " + pending.PackageName + " " + pending.VersionCode
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
    return Results.Ok(new
    {
        ok = true,
        state = device.State,
        certSha256 = AgentDesk.CertStamp(portal.CertPath),
        certificates = portal.PublicBaseUrl.TrimEnd('/') + "/store/portal.crt",
        update = pending is null ? null : new
        {
            jobId = pending.JobId,
            packageName = pending.PackageName,
            versionCode = pending.VersionCode,
            sha256 = pending.Sha256,
            url = pending.Url
        },
        session = session is null ? null : new { sessionId = session.Id, fps = session.Fps, width = session.Width }
    });
});

app.MapPost("/api/agent/jobs/{id}", async (string id, AgentCall body, PortalDb db, AgentDesk desk, CancellationToken cancellationToken) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == body.DeviceId, cancellationToken);
    if (device is null || !AgentDesk.Signed(device, body.Nonce, body.Signature, "|" + id))
        return Results.Json(new { ok = false, error = "подпись устройства не сошлась" }, statusCode: StatusCodes.Status401Unauthorized);
    var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id && x.DeviceId == device.Id, cancellationToken);
    if (job is null)
        return Results.NotFound();
    var version = string.IsNullOrWhiteSpace(job.VersionId)
        ? null
        : await db.Versions.FirstOrDefaultAsync(x => x.Id == job.VersionId, cancellationToken);
    var package = version is null
        ? null
        : await db.Packages.FirstOrDefaultAsync(x => x.Id == version.PackageId, cancellationToken);
    var agentStillOld = package?.PackageName == AgentJobs.AgentPackage
        && version is not null
        && body.Ok
        && (device.AgentCode ?? 0) < version.VersionCode;
    if (agentStillOld)
    {
        job.State = JobStates.Failed;
        job.Error = "телефон ещё не сообщил новую версию";
    }
    else
    {
        job.State = body.Ok ? JobStates.Succeeded : JobStates.Failed;
        job.Error = body.Error;
    }
    job.FinishedAt = DateTime.UtcNow;
    device.LastSeenAt = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    desk.Release(id);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/agent/session/{id}/frame", async (string id, HttpRequest request, PortalDb db, AgentDesk desk, CancellationToken cancellationToken) =>
{
    var device = await AuthorizedDevice(request, id, db, cancellationToken);
    if (device is null)
        return Results.Unauthorized();
    using var memory = new MemoryStream();
    await request.Body.CopyToAsync(memory, cancellationToken);
    var bytes = memory.ToArray();
    if (bytes.Length is 0 or > 2_000_000)
        return Results.BadRequest();
    desk.Frame(device.Id, id, bytes);
    device.LastSeenAt = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok();
});

app.MapPost("/api/agent/session/{id}/status", async (string id, AgentCall body, PortalDb db, AgentDesk desk, CancellationToken cancellationToken) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == body.DeviceId, cancellationToken);
    if (device is null || !AgentDesk.Signed(device, body.Nonce, body.Signature, "|" + id))
        return Results.Unauthorized();
    desk.Fail(device.Id, id, body.Error ?? "экран недоступен");
    return Results.Ok();
});

app.MapPost("/api/agent/session/{id}/input", async (string id, AgentCall body, PortalDb db, AgentDesk desk, CancellationToken cancellationToken) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == body.DeviceId, cancellationToken);
    if (device is null || !AgentDesk.Signed(device, body.Nonce, body.Signature, "|" + id))
        return Results.Unauthorized();
    var until = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < until && desk.Session(device.Id)?.Id == id && !desk.HasInputs(device.Id, id))
        await Task.Delay(200, cancellationToken);
    return Results.Ok(new { events = desk.TakeInputs(device.Id, id) });
});

app.MapPost("/devices/{id}/live/start", async (string id, HttpContext http, PortalDb db, AgentDesk desk) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == id);
    if (device is null || device.State != DeviceStates.Managed)
        return Results.NotFound();
    var session = desk.Open(id, http.User.Identity?.Name ?? "");
    return Results.Ok(new { sessionId = session.Id });
}).RequireAuthorization();

app.MapPost("/devices/{id}/live/stop", (string id, AgentDesk desk) =>
{
    desk.Close(id);
    return Results.Ok();
}).RequireAuthorization();

app.MapPost("/devices/{id}/live/input", async (string id, LiveInput input, AgentDesk desk) =>
{
    desk.Input(id, input);
    return Results.Ok();
}).RequireAuthorization();

app.MapGet("/devices/{id}/live/frame", (string id, AgentDesk desk) =>
{
    var session = desk.Session(id);
    if (session?.Frame is null)
        return Results.NoContent();
    return Results.File(session.Frame, "image/jpeg");
}).RequireAuthorization();

app.MapGet("/devices/{id}/live/status", (string id, AgentDesk desk) =>
{
    var session = desk.Session(id);
    return Results.Ok(new { connected = session is not null, error = session?.Error, hasFrame = session?.Frame is not null });
}).RequireAuthorization();

app.MapGet("/store/versions/{id}/file", async (string id, PortalDb db) =>
{
    var version = await db.Versions.FirstOrDefaultAsync(x => x.Id == id);
    if (version is null)
        return Results.NotFound();
    var path = CatalogLayout.VersionPath(Path.Combine(portal.DataDir, "catalog"), version.PackageId, version.Id, version.FileName);
    if (!File.Exists(path))
        return Results.NotFound();
    return Results.File(path, "application/octet-stream", version.FileName);
});

app.MapGet("/store/catalog.json", async (PortalDb db) =>
{
    var packages = await db.Packages.OrderBy(x => x.Title).ToListAsync();
    var versions = await db.Versions.Where(x => x.Assignable).OrderByDescending(x => x.VersionCode).ToListAsync();
    var items = new List<object>();
    foreach (var package in packages)
    {
        var version = versions.FirstOrDefault(x => x.PackageId == package.Id);
        if (version is null)
            continue;
        items.Add(new
        {
            title = package.Title,
            packageName = package.PackageName,
            summary = package.Summary ?? "",
            versionName = version.VersionName,
            versionCode = version.VersionCode,
            sha256 = version.Sha256,
            url = portal.PublicBaseUrl.TrimEnd('/') + "/store/versions/" + version.Id + "/file"
        });
    }
    return Results.Json(new { items });
});

app.MapGet("/store/bootstrap.json", (SiteCertificate site) =>
{
    var body = JsonSerializer.Serialize(new
    {
        server = portal.PublicBaseUrl.TrimEnd('/'),
        fingerprint = site.Formatted,
        site = portal.SiteName
    });
    return Results.Text(body, "application/json");
});

app.MapGet("/store/portal.crt", () =>
    Results.File(portal.CertPath, "application/x-x509-ca-cert", "portal.crt"));

app.MapGet("/api/agent/files/versions/{id}", async (string id, HttpRequest request, PortalDb db) =>
{
    var deviceId = request.Headers["X-Device-Id"].ToString();
    var signature = request.Headers["X-Signature"].ToString();
    var version = await db.Versions.FirstOrDefaultAsync(x => x.Id == id);
    var device = string.IsNullOrWhiteSpace(deviceId) ? null : await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId);
    if (version is null || device is null || device.State != DeviceStates.Managed || string.IsNullOrWhiteSpace(device.PublicKey))
        return Results.Unauthorized();
    if (!Protocol.VerifySignature(device.PublicKey, $"{deviceId}|{version.Id}|{version.Sha256}", signature))
        return Results.Unauthorized();
    var allowed = await db.Assignments.AnyAsync(x => x.GroupId == device.GroupId && x.VersionId == version.Id)
        || await db.Jobs.AnyAsync(x => x.DeviceId == device.Id && x.VersionId == version.Id);
    if (!allowed)
        return Results.Forbid();
    var path = CatalogLayout.VersionPath(Path.Combine(portal.DataDir, "catalog"), version.PackageId, version.Id, version.FileName);
    if (!File.Exists(path))
        return Results.NotFound();
    return Results.File(path, "application/vnd.android.package-archive", version.FileName);
});

app.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync();
    return Results.Redirect("/");
}).RequireAuthorization();

app.Run();

static async Task<Device?> AuthorizedDevice(HttpRequest request, string sessionId, PortalDb db, CancellationToken cancellationToken)
{
    var deviceId = request.Headers["X-Device-Id"].ToString();
    var nonce = request.Headers["X-Nonce"].ToString();
    var signature = request.Headers["X-Signature"].ToString();
    var device = string.IsNullOrWhiteSpace(deviceId) ? null : await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
    if (device is null || !AgentDesk.Signed(device, nonce, signature, "|" + sessionId))
        return null;
    return device;
}

static string ClientAddress(HttpContext http)
{
    var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
    var raw = string.IsNullOrWhiteSpace(forwarded)
        ? http.Connection.RemoteIpAddress?.ToString() ?? ""
        : forwarded.Split(',')[0].Trim();
    const string mapped = "::ffff:";
    if (raw.StartsWith(mapped, StringComparison.OrdinalIgnoreCase))
        raw = raw[mapped.Length..];
    return raw;
}

public sealed class SiteCertificate
{
    readonly PortalOptions _options;

    public SiteCertificate(PortalOptions options) => _options = options;

    public string Hex
    {
        get
        {
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(_options.CertPath));
            return Protocol.Fingerprint(cert.RawData);
        }
    }

    public string Formatted => Protocol.FormatFingerprint(Hex);
}

public sealed class ScanCoordinator
{
    readonly object _gate = new();
    public bool Running { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public string? Error { get; private set; }

    public bool Start(IServiceProvider services)
    {
        lock (_gate)
        {
            if (Running)
                return false;
            Running = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PortalDb>();
                var discovery = scope.ServiceProvider.GetRequiredService<DiscoveryService>();
                var options = scope.ServiceProvider.GetRequiredService<PortalOptions>();
                await discovery.ScanAsync(db, options, CancellationToken.None);
                Error = null;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                FinishedAt = DateTime.UtcNow;
                Running = false;
            }
        });
        return true;
    }
}

public static class PortalSeed
{
    public static async Task EnsureAsync(PortalDb db, PortalOptions options)
    {
        if (!await db.Users.AnyAsync())
        {
            if (string.IsNullOrWhiteSpace(options.AdminPassword))
                throw new InvalidOperationException("Задайте Portal__AdminPassword");
            db.Users.Add(new PortalUser
            {
                Id = Guid.NewGuid().ToString("D"),
                Login = string.IsNullOrWhiteSpace(options.AdminLogin) ? "admin" : options.AdminLogin,
                PasswordHash = Passwords.Hash(options.AdminPassword),
                Role = Roles.Admin,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await ImportSeedAsync(db, options);
        await CatalogHistory.TrimAsync(db, options.DataDir);
    }

    public static async Task ImportSeedAsync(PortalDb db, PortalOptions options)
    {
        var apk = Path.Combine(options.DataDir, "seed", "ru.bytplast.tsdagent.apk");
        if (!File.Exists(apk))
            return;
        var info = ApkInspector.Read(apk);
        var title = "Агент ТСД";
        var summary = "Приложение площадки: доступность терминала, установка пакетов и удалённое управление.";
        var marker = Path.Combine(options.DataDir, "seed", "agent.json");
        if (File.Exists(marker))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
            if (json.RootElement.TryGetProperty("title", out var titleNode))
                title = titleNode.GetString() ?? title;
            if (json.RootElement.TryGetProperty("summary", out var summaryNode))
                summary = summaryNode.GetString() ?? summary;
        }
        var package = await db.Packages.FirstOrDefaultAsync(x => x.PackageName == info.PackageName);
        if (package is null)
        {
            package = new CatalogPackage
            {
                Id = Guid.NewGuid().ToString("D"),
                Kind = PackageKinds.Agent,
                Title = title,
                PackageName = info.PackageName,
                Summary = summary,
                CreatedAt = DateTime.UtcNow
            };
            db.Packages.Add(package);
        }
        if (await db.Versions.AnyAsync(x => x.PackageId == package.Id && x.VersionCode == info.VersionCode))
        {
            await db.SaveChangesAsync();
            return;
        }
        var version = new PackageVersion
        {
            Id = Guid.NewGuid().ToString("D"),
            PackageId = package.Id,
            VersionCode = info.VersionCode,
            VersionName = info.VersionName,
            Sha256 = ApkInspector.Sha256(apk),
            FileName = info.PackageName + "-" + info.VersionName + ".apk",
            Size = new FileInfo(apk).Length,
            Assignable = true,
            CreatedAt = DateTime.UtcNow
        };
        var dest = CatalogLayout.VersionPath(Path.Combine(options.DataDir, "catalog"), package.Id, version.Id, version.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(apk, dest, true);
        db.Versions.Add(version);
        db.Audit.Add(new AuditEntry
        {
            At = DateTime.UtcNow,
            User = "system",
            Action = "catalog.seed",
            Detail = info.PackageName + " " + info.VersionName
        });
        await db.SaveChangesAsync();
    }
}

public sealed class ReconcileWorker : BackgroundService
{
    readonly IServiceProvider _services;
    readonly PortalOptions _options;
    readonly AgentDesk _desk;

    public ReconcileWorker(IServiceProvider services, PortalOptions options, AgentDesk desk)
    {
        _services = services;
        _options = options;
        _desk = desk;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Следующий цикл сверки повторит устройства, до которых не дошли.
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDb>();
        var channel = scope.ServiceProvider.GetRequiredService<AgentChannel>();
        var devices = await db.Devices.Where(x => x.State == DeviceStates.Managed).ToListAsync(cancellationToken);
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.LastAddress) || string.IsNullOrWhiteSpace(device.PublicKey))
                continue;
            try
            {
                var report = await channel.GetReportAsync(device.LastAddress, device.PublicKey, cancellationToken);
                if (report is null)
                {
                    if (device.LastSeenAt is null || device.LastSeenAt < DateTime.UtcNow.AddMinutes(-2))
                        device.LastError = "нет ответа на запрос отчёта";
                }
                else
                {
                    device.LastSeenAt = DateTime.UtcNow;
                    device.AgentCode = report.AgentCode;
                    device.AgentName = report.AgentName;
                    device.LastError = string.IsNullOrWhiteSpace(report.LastError) ? null : report.LastError;
                    device.ReportedPackagesJson = JsonSerializer.Serialize(report.Packages ?? []);
                    device.CompletedBlocksJson = JsonSerializer.Serialize(report.CompletedBlocks ?? []);
                    await SendInstallAsync(db, channel, device, report, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                if (device.LastSeenAt is null || device.LastSeenAt < DateTime.UtcNow.AddMinutes(-2))
                    device.LastError = ex.Message;
            }
            await PushAgentUpdateAsync(db, channel, _desk, device, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    async Task SendInstallAsync(PortalDb db, AgentChannel channel, Device device, AgentReport report, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.GroupId))
            return;
        var stale = await db.Jobs.Where(x => x.DeviceId == device.Id && x.State == JobStates.Sent && x.CreatedAt < DateTime.UtcNow.AddMinutes(-10)).ToListAsync(cancellationToken);
        foreach (var job in stale)
        {
            job.State = JobStates.Failed;
            job.Error = "таймаут";
            job.FinishedAt = DateTime.UtcNow;
        }
        if (await db.Jobs.AnyAsync(x => x.DeviceId == device.Id && (x.State == JobStates.Queued || x.State == JobStates.Sent), cancellationToken))
            return;
        var assignments = await db.Assignments.Where(x => x.GroupId == device.GroupId).ToListAsync(cancellationToken);
        foreach (var assignment in assignments)
        {
            var version = await db.Versions.FirstOrDefaultAsync(x => x.Id == assignment.VersionId, cancellationToken);
            var package = version is null ? null : await db.Packages.FirstOrDefaultAsync(x => x.Id == version.PackageId, cancellationToken);
            if (version is null || package is null)
                continue;
            var installed = report.Packages?.FirstOrDefault(x => x.PackageName == package.PackageName);
            if (installed is not null && installed.VersionCode == version.VersionCode)
                continue;
            if (package.Kind == PackageKinds.Agent && report.AgentCode == version.VersionCode)
                continue;
            var recentFailure = await db.Jobs.AnyAsync(x =>
                x.DeviceId == device.Id && x.VersionId == version.Id && x.State == JobStates.Failed &&
                x.FinishedAt > DateTime.UtcNow.AddMinutes(-10), cancellationToken);
            if (recentFailure)
                continue;
            var job = new Job
            {
                Id = Guid.NewGuid().ToString("D"),
                DeviceId = device.Id,
                Kind = "install",
                VersionId = version.Id,
                State = JobStates.Sent,
                CreatedAt = DateTime.UtcNow
            };
            db.Jobs.Add(job);
            var answer = await channel.InstallAsync(device.LastAddress!, device.PublicKey!, new
            {
                type = "install",
                jobId = job.Id,
                kind = "apk",
                packageName = package.PackageName,
                versionCode = version.VersionCode,
                sha256 = version.Sha256,
                url = portalBase() + "/api/agent/files/versions/" + version.Id,
                allowDowngrade = false
            }, cancellationToken);
            job.State = answer is { Ok: true } ? JobStates.Succeeded : JobStates.Failed;
            job.Error = answer?.Error;
            job.FinishedAt = DateTime.UtcNow;
            return;
        }

        string portalBase() => _options.PublicBaseUrl.TrimEnd('/');
    }

    async Task PushAgentUpdateAsync(PortalDb db, AgentChannel channel, AgentDesk desk, Device device, CancellationToken cancellationToken)
    {
        if (device.State != DeviceStates.Managed || string.IsNullOrWhiteSpace(device.LastAddress) || string.IsNullOrWhiteSpace(device.PublicKey))
            return;
        if (device.LastSeenAt is not null && device.LastSeenAt > DateTime.UtcNow.AddSeconds(-90))
            return;
        var offer = await AgentJobs.NextAsync(db, device, _options.PublicBaseUrl, cancellationToken);
        if (offer is null || !desk.Claim(offer.JobId))
            return;
        var job = await db.Jobs.FirstAsync(x => x.Id == offer.JobId, cancellationToken);
        try
        {
            var answer = await channel.InstallAsync(device.LastAddress, device.PublicKey, new
            {
                type = "install",
                jobId = offer.JobId,
                kind = "apk",
                packageName = offer.PackageName,
                versionCode = offer.VersionCode,
                sha256 = offer.Sha256,
                url = offer.Url,
                allowDowngrade = false
            }, cancellationToken);
            job.State = answer is { Ok: true } ? JobStates.Succeeded : JobStates.Failed;
            job.Error = answer?.Error;
            job.FinishedAt = DateTime.UtcNow;
            desk.Release(offer.JobId);
        }
        catch (Exception ex)
        {
            job.State = JobStates.Queued;
            job.Error = ex.Message;
            db.AgentLogs.Add(new AgentLog
            {
                DeviceId = device.Id,
                At = DateTime.UtcNow,
                Area = "portal",
                Text = "входящая установка не дошла: " + ex.Message
            });
            desk.Release(offer.JobId);
        }
    }
}

