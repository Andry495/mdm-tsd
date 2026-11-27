using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TsdServer.Infrastructure;

public sealed record ApkInfo(string PackageName, int VersionCode, string VersionName);

public static class ApkInspector
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static ApkInfo Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("AndroidManifest.xml")
            ?? throw new InvalidDataException("В APK нет AndroidManifest.xml");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return ReadManifest(memory.ToArray());
    }

    public static ApkInfo ReadManifest(byte[] xml)
    {
        var reader = new Reader(xml);
        var fileType = reader.U16();
        var fileHeader = reader.U16();
        _ = reader.U32();
        if (fileType != 0x0003 || fileHeader < 8)
            throw new InvalidDataException("Манифест APK не в двоичном XML");

        var poolType = reader.U16();
        var poolHeader = reader.U16();
        var poolSize = reader.U32();
        if (poolType != 0x0001)
            throw new InvalidDataException("В манифесте нет пула строк");
        var poolStart = reader.Position - 8;
        var stringCount = checked((int)reader.U32());
        _ = reader.U32();
        var flags = reader.U32();
        var stringsStart = reader.U32();
        _ = reader.U32();
        if (stringCount < 0 || stringCount > 100_000)
            throw new InvalidDataException("Странный пул строк манифеста");
        var offsets = new int[stringCount];
        for (var i = 0; i < stringCount; i++)
            offsets[i] = checked((int)reader.U32());
        var utf8 = (flags & (1u << 8)) != 0;
        var strings = new string[stringCount];
        var dataStart = poolStart + (int)stringsStart;
        for (var i = 0; i < stringCount; i++)
            strings[i] = ReadString(xml, dataStart + offsets[i], utf8);

        reader.Position = poolStart + (int)poolSize;
        string? packageName = null;
        int? versionCode = null;
        string? versionName = null;
        while (reader.Remaining >= 8)
        {
            var chunkStart = reader.Position;
            var type = reader.U16();
            _ = reader.U16();
            var size = (int)reader.U32();
            if (size < 8 || chunkStart + size > xml.Length)
                break;
            if (type == 0x0102)
            {
                var headerSize = xml[chunkStart + 2] | (xml[chunkStart + 3] << 8);
                _ = reader.U32();
                _ = reader.U32();
                var first = (int)reader.U32();
                var second = (int)reader.U32();
                var attributeStart = reader.U16();
                var attributeSize = reader.U16();
                var attributeCount = reader.U16();
                var elementName = At(strings, second) ?? At(strings, first);
                var attrBase = attributeStart >= 36
                    ? chunkStart + attributeStart
                    : chunkStart + headerSize + attributeStart;
                if (elementName == "manifest")
                {
                    for (var i = 0; i < attributeCount; i++)
                    {
                        var attr = attrBase + i * attributeSize;
                        if (attributeSize < 20 || attr + 20 > xml.Length)
                            break;
                        var attrName = At(strings, (int)ReadU32(xml, attr + 4));
                        var raw = ReadU32(xml, attr + 8);
                        var dataType = xml[attr + 15];
                        var data = ReadU32(xml, attr + 16);
                        if (attrName == "package" && dataType == 0x03)
                            packageName = At(strings, (int)data) ?? At(strings, (int)raw);
                        else if (attrName == "versionName" && dataType == 0x03)
                            versionName = At(strings, (int)data) ?? At(strings, (int)raw);
                        else if (attrName == "versionCode" && dataType is 0x10 or 0x11 or 0x12)
                            versionCode = unchecked((int)data);
                    }
                }
            }
            reader.Position = chunkStart + size;
        }

        if (string.IsNullOrWhiteSpace(packageName) || versionCode is null)
            throw new InvalidDataException("Из APK не удалось прочитать имя пакета и versionCode");
        return new ApkInfo(packageName, versionCode.Value, versionName ?? versionCode.Value.ToString());
    }

    static string? At(string[] strings, int index) =>
        index >= 0 && index < strings.Length ? strings[index] : null;

    static uint ReadU32(byte[] xml, int at) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(xml.AsSpan(at));

    static string ReadString(byte[] xml, int at, bool utf8)
    {
        if (utf8)
        {
            var (charLen, p) = Utf8Len(xml, at);
            var (byteLen, start) = Utf8Len(xml, p);
            _ = charLen;
            if (start + byteLen > xml.Length)
                return "";
            return Encoding.UTF8.GetString(xml, start, byteLen);
        }
        var count = xml[at] | (xml[at + 1] << 8);
        var start16 = at + 2;
        if ((count & 0x8000) != 0)
        {
            var count2 = xml[at + 2] | (xml[at + 3] << 8);
            count = ((count & 0x7fff) << 16) | count2;
            start16 = at + 4;
        }
        if (start16 + count * 2 > xml.Length)
            return "";
        return Encoding.Unicode.GetString(xml, start16, count * 2);
    }

    static (int len, int next) Utf8Len(byte[] xml, int at)
    {
        var first = xml[at];
        if ((first & 0x80) == 0)
            return (first, at + 1);
        var second = xml[at + 1];
        return (((first & 0x7f) << 8) | second, at + 2);
    }

    sealed class Reader
    {
        readonly byte[] _bytes;
        public int Position { get; set; }
        public int Remaining => _bytes.Length - Position;
        public Reader(byte[] bytes) => _bytes = bytes;
        public ushort U16()
        {
            var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(Position));
            Position += 2;
            return v;
        }
        public uint U32()
        {
            var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(Position));
            Position += 4;
            return v;
        }
    }
}

public static class CatalogLayout
{
    public static string VersionPath(string root, string packageId, string versionId, string fileName) =>
        Path.Combine(root, packageId, versionId, fileName);

    public static void WriteSeedMarker(string path, ApkInfo info, string fileName, string title, string summary)
    {
        var json = JsonSerializer.Serialize(new
        {
            title,
            packageName = info.PackageName,
            versionCode = info.VersionCode,
            versionName = info.VersionName,
            file = fileName,
            summary
        });
        File.WriteAllText(path, json);
    }
}
