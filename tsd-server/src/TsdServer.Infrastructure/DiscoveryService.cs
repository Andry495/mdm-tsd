using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TsdServer.Core;

namespace TsdServer.Infrastructure;

public sealed class PortalOptions
{
    public string DataDir { get; set; } = "/var/lib/tsd-portal";
    public string[] Cidrs { get; set; } = [];
    public int DiscoveryPort { get; set; } = Protocol.DiscoveryPort;
    public int Parallelism { get; set; } = 64;
    public int ConnectTimeoutMs { get; set; } = 300;
    public int CommandPort { get; set; } = Protocol.CommandPort;
    public int ReconcileIntervalSeconds { get; set; } = 60;
    public string PublicBaseUrl { get; set; } = "";
    public string SiteName { get; set; } = "Бытпласт";
    public string CertPath { get; set; } = "/certs/portal.crt";
    public string CertKeyPath { get; set; } = "/certs/portal.key";
    public string AdminLogin { get; set; } = "admin";
    public string AdminPassword { get; set; } = "";
}

public sealed class DiscoveryService
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ScanAsync(PortalDb db, PortalOptions options, CancellationToken cancellationToken)
    {
        var addresses = options.Cidrs.SelectMany(Expand).Distinct().ToArray();
        var gate = new SemaphoreSlim(1, 1);
        await Parallel.ForEachAsync(addresses, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Parallelism),
            CancellationToken = cancellationToken
        }, async (ip, token) =>
        {
            Announce? announce;
            try
            {
                announce = await ProbeAsync(ip, options, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return;
            }
            if (announce is null)
                return;
            await gate.WaitAsync(token);
            try
            {
                await UpsertAsync(db, announce, ip);
                await db.SaveChangesAsync(token);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    public static async Task<Announce?> ProbeAsync(string ip, PortalOptions options, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeoutMs);
        try
        {
            await client.ConnectAsync(ip, options.DiscoveryPort, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        client.ReceiveTimeout = Math.Max(options.ConnectTimeoutMs, 500);
        client.SendTimeout = Math.Max(options.ConnectTimeoutMs, 500);
        var stream = client.GetStream();
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var request = JsonSerializer.Serialize(new { type = "discover", nonce, server = "tsd-server" }, Json);
        var payload = Encoding.UTF8.GetBytes(request + "\n");
        var packet = new byte[Protocol.Magic.Length + payload.Length];
        Protocol.Magic.CopyTo(packet, 0);
        payload.CopyTo(packet, Protocol.Magic.Length);
        await stream.WriteAsync(packet, cancellationToken);
        var line = await ReadLineAsync(stream, cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
            return null;
        Announce? announce;
        try
        {
            announce = JsonSerializer.Deserialize<Announce>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (announce is null || announce.Type != "announce" || announce.Protocol != 1 || string.IsNullOrWhiteSpace(announce.DeviceId))
            return null;
        if (!Protocol.VerifySignature(announce.PublicKey, nonce + "|" + announce.DeviceId, announce.Signature))
            return null;
        return announce;
    }

    public async Task<string> RegisterAsync(PortalDb db, Announce announce, string ip, CancellationToken cancellationToken = default)
    {
        if (announce.Protocol != 1 || announce.Type != "announce" || string.IsNullOrWhiteSpace(announce.DeviceId) || string.IsNullOrWhiteSpace(announce.PublicKey))
            throw new InvalidOperationException("неполное сообщение");
        if (!long.TryParse(announce.Nonce, out var unix))
            throw new InvalidOperationException("нет отметки времени");
        var skew = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix);
        if (skew > 15 * 60)
            throw new InvalidOperationException("время устройства отличается больше чем на 15 минут");
        if (!Protocol.VerifySignature(announce.PublicKey, announce.Nonce + "|" + announce.DeviceId, announce.Signature))
            throw new UnauthorizedAccessException("подпись устройства не сошлась");
        await UpsertAsync(db, announce, ip);
        var current = db.Devices.Local.First(x => x.Id == announce.DeviceId);
        await CollapseTwinsAsync(db, current, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var device = await db.Devices.FirstAsync(x => x.Id == announce.DeviceId, cancellationToken);
        return device.State;
    }

    static async Task UpsertAsync(PortalDb db, Announce announce, string ip)
    {
        var now = DateTime.UtcNow;
        var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == announce.DeviceId);
        if (device is null)
        {
            db.Devices.Add(new Device
            {
                Id = announce.DeviceId,
                Serial = announce.Serial,
                Manufacturer = announce.Manufacturer,
                Name = string.IsNullOrWhiteSpace(announce.Name) ? null : announce.Name.Trim(),
                Model = announce.Model,
                Android = announce.Android,
                SuggestedFamily = Families.Suggest(announce.Manufacturer, announce.Model),
                State = DeviceStates.Pending,
                LastAddress = ip,
                PublicKey = announce.PublicKey,
                AgentName = announce.AgentName,
                AgentCode = announce.AgentCode,
                LastSeenAt = now,
                CreatedAt = now
            });
            return;
        }
        if (!string.IsNullOrWhiteSpace(announce.Name))
            device.Name = announce.Name.Trim();
        if (device.State == DeviceStates.Rejected)
        {
            device.LastSeenAt = now;
            device.LastAddress = ip;
            return;
        }
        if (device.State == DeviceStates.Managed && !string.Equals(device.PublicKey, announce.PublicKey, StringComparison.Ordinal))
        {
            device.LastError = "ключ устройства не совпал с сохранённым";
            device.LastSeenAt = now;
            return;
        }
        device.Serial = announce.Serial;
        device.Manufacturer = announce.Manufacturer;
        device.Model = announce.Model;
        device.Android = announce.Android;
        device.SuggestedFamily = Families.Suggest(announce.Manufacturer, announce.Model);
        device.LastAddress = ip;
        device.AgentName = announce.AgentName;
        device.AgentCode = announce.AgentCode;
        device.PublicKey = announce.PublicKey;
        device.LastSeenAt = now;
        device.LastError = null;
    }

    public static async Task CollapseTwinsAsync(PortalDb db, Device current, CancellationToken cancellationToken)
    {
        var others = await db.Devices.Where(x => x.Id != current.Id).ToListAsync(cancellationToken);
        foreach (var old in others)
        {
            var sameAndroid = !string.IsNullOrWhiteSpace(current.AndroidId) && current.AndroidId == old.AndroidId;
            var sameSerial = !string.IsNullOrWhiteSpace(current.Serial) && current.Serial == old.Serial;
            var samePlace = string.Equals(current.Manufacturer, old.Manufacturer, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Model, old.Model, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(current.LastAddress)
                && current.LastAddress == old.LastAddress
                && (old.LastSeenAt is null || old.LastSeenAt < DateTime.UtcNow.AddMinutes(-10));
            if (!sameAndroid && !sameSerial && !samePlace)
                continue;
            if (old.State == DeviceStates.Managed && current.State == DeviceStates.Pending)
            {
                current.State = DeviceStates.Managed;
                current.Family ??= old.Family;
                current.GroupId ??= old.GroupId;
            }
            var jobs = await db.Jobs.Where(x => x.DeviceId == old.Id).ToListAsync(cancellationToken);
            db.Jobs.RemoveRange(jobs);
            var logs = await db.AgentLogs.Where(x => x.DeviceId == old.Id).ToListAsync(cancellationToken);
            db.AgentLogs.RemoveRange(logs);
            db.Devices.Remove(old);
            db.AgentLogs.Add(new AgentLog
            {
                DeviceId = current.Id,
                At = DateTime.UtcNow,
                Area = "link",
                Text = "убрана повторная карточка " + (old.AgentName ?? old.Id)
            });
        }
    }

    static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        while (memory.Length < 65536)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                memory.Write(buffer, 0, newline);
                break;
            }
            memory.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(memory.ToArray()).Trim();
    }

    public static IEnumerable<string> Expand(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var bits) || bits is < 0 or > 32)
            yield break;
        var octets = parts[0].Split('.');
        if (octets.Length != 4)
            yield break;
        uint ip = 0;
        foreach (var octet in octets)
        {
            if (!byte.TryParse(octet, out var value))
                yield break;
            ip = (ip << 8) | value;
        }
        var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        var network = ip & mask;
        var broadcast = network | ~mask;
        var hostBits = 32 - bits;
        var count = hostBits >= 31 ? 0 : (1u << hostBits) - 2;
        if (bits >= 31)
        {
            yield return Format(network);
            if (bits == 31)
                yield return Format(broadcast);
            yield break;
        }
        for (var i = 1u; i <= count; i++)
            yield return Format(network + i);
    }

    static string Format(uint ip) => $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";
}

public sealed class Announce
{
    public string Type { get; set; } = "";
    public int Protocol { get; set; }
    public string DeviceId { get; set; } = "";
    public string? Serial { get; set; }
    public string? Manufacturer { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
    public string? Android { get; set; }
    public string? AgentName { get; set; }
    public int? AgentCode { get; set; }
    public string PublicKey { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Nonce { get; set; } = "";
}
