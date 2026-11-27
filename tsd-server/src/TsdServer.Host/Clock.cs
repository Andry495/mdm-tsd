using TsdServer.Core;

namespace TsdServer.Host;

public static class Clock
{
    static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    public static string When(DateTime? utc) =>
        utc is null
            ? "нет"
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), Moscow).ToString("dd.MM.yyyy HH:mm");

    public static bool Online(Device device) =>
        device.LastSeenAt is not null && DateTime.UtcNow - device.LastSeenAt.Value < TimeSpan.FromMinutes(3);
}
