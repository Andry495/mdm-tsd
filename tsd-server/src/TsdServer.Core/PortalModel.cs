namespace TsdServer.Core;

public static class Roles
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";
}

public static class DeviceStates
{
    public const string Pending = "pending";
    public const string Managed = "managed";
    public const string Rejected = "rejected";
}

public static class PackageKinds
{
    public const string Agent = "agent";
    public const string Apk = "apk";
}

public static class JobStates
{
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class Families
{
    public const string ZebraMc33 = "zebra_mc33";
    public const string NewlandMt65 = "newland_mt65";
    public const string Phone = "phone";

    public static readonly string[] All = [ZebraMc33, NewlandMt65, Phone];

    public static string Suggest(string? manufacturer, string? model)
    {
        var text = $"{manufacturer} {model}".ToUpperInvariant();
        if (text.Contains("MC33", StringComparison.Ordinal))
            return ZebraMc33;
        if (text.Contains("MT65", StringComparison.Ordinal) || text.Contains("NEWLAND", StringComparison.Ordinal))
            return NewlandMt65;
        return Phone;
    }

    public static string Title(string family) => family switch
    {
        ZebraMc33 => "Zebra MC33",
        NewlandMt65 => "Newland MT65",
        Phone => "Телефон",
        _ => family
    };
}

public sealed class PortalUser
{
    public string Id { get; set; } = "";
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public DateTime CreatedAt { get; set; }
}

public sealed class DeviceGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Family { get; set; } = Families.Phone;
    public bool OnecChecked { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Device
{
    public string Id { get; set; } = "";
    public string? Serial { get; set; }
    public string? Manufacturer { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }

    public string Title() => string.IsNullOrWhiteSpace(Name) ? Model ?? Id : Name;
    public string? Android { get; set; }
    public string SuggestedFamily { get; set; } = Families.Phone;
    public string? Family { get; set; }
    public string? GroupId { get; set; }
    public string State { get; set; } = DeviceStates.Pending;
    public string? LastAddress { get; set; }
    public string? PublicKey { get; set; }
    public string? AgentName { get; set; }
    public int? AgentCode { get; set; }
    public string? ReportedPackagesJson { get; set; }
    public string? CompletedBlocksJson { get; set; }
    public string? LastError { get; set; }
    public string? AndroidId { get; set; }
    public string? UpdatePhase { get; set; }
    public string? ScreenPhase { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AgentLog
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTime At { get; set; }
    public string Area { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class CatalogPackage
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = PackageKinds.Apk;
    public string Title { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string? Summary { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class PackageVersion
{
    public string Id { get; set; } = "";
    public string PackageId { get; set; } = "";
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public bool Assignable { get; set; } = true;
    public string? ImeComponent { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Assignment
{
    public string Id { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public bool Required { get; set; } = true;
}

public sealed class Job
{
    public string Id { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Kind { get; set; } = "install";
    public string? VersionId { get; set; }
    public string State { get; set; } = JobStates.Queued;
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}

public static class Passwords
{
    public static string Hash(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.', 2);
        if (parts.Length != 2)
            return false;
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expected = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hash, expected);
    }
}
