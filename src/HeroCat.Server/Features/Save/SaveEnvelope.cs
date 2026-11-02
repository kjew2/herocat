using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeroCat.Server.Features.Save;

/// <summary>Why an uploaded file is not a save (step 1 of specs/08 section 3.1; <c>400 SAVE_MALFORMED</c>).</summary>
public sealed class SaveMalformedException : Exception
{
    public SaveMalformedException(string reason) : base(reason) { }
}

/// <summary>
/// The schema v1 save container (specs/02 section 5.1), as the server reads it: one ASCII header line and exactly
/// <c>len</c> bytes of UTF-8 JSON.
/// <code>HEROCAT-SAVE v=1 fmt=json crc32=7f3a19c2 len=38127 hmac=&lt;64 hex or -&gt;\n{ ... }</code>
/// The server does not reference HeroCat.Runtime (01 section 1.2), so this is its own reader of the same format; the
/// body fields it needs (the root's <c>SchemaVersion</c>, <c>PlayerId</c>, <c>DeviceId</c>, <c>SaveSeq</c>,
/// <c>ContentVersion</c> and <c>Time.LastSavedServerTime</c>) are read straight from the JSON.
/// </summary>
public sealed class SaveEnvelope
{
    public const string Magic = "HEROCAT-SAVE";
    const int MaxHeaderBytes = 256;

    SaveEnvelope(int bodyOffset, int bodyLength, string hmacHex, byte[] sha256)
    {
        BodyOffset = bodyOffset;
        BodyLength = bodyLength;
        HmacHex = hmacHex;
        Sha256 = sha256;
    }

    public int BodyOffset { get; }
    public int BodyLength { get; }

    /// <summary>The header's <c>hmac</c> field: 64 lowercase hex, or <c>-</c> (Local mode, no key).</summary>
    public string HmacHex { get; }

    /// <summary>SHA-256 of the body bytes (the <c>sha256</c> of 02 section 9.1, as Runtime's <c>SaveFile.Sha256</c> computes it).</summary>
    public byte[] Sha256 { get; }

    public int SchemaVersion { get; private set; }
    public Guid PlayerId { get; private set; }
    public Guid DeviceId { get; private set; }
    public long SaveSeq { get; private set; }
    public string ContentVersion { get; private set; } = "";
    public long LastSavedServerTime { get; private set; }

    /// <summary>Reads the header, checks <c>len</c> and the CRC, hashes the body and reads its root fields.</summary>
    public static SaveEnvelope Parse(byte[] file)
    {
        if (file.Length == 0) throw new SaveMalformedException("empty file");
        int nl = Array.IndexOf(file, (byte)'\n', 0, Math.Min(file.Length, MaxHeaderBytes));
        if (nl < 0) throw new SaveMalformedException("no header line");
        string header = Encoding.ASCII.GetString(file, 0, nl);
        var parts = header.Split(' ');
        if (parts.Length != 6 || parts[0] != Magic) throw new SaveMalformedException("bad header");
        string v = Field(parts[1], "v"), fmt = Field(parts[2], "fmt"), crcHex = Field(parts[3], "crc32"),
               lenStr = Field(parts[4], "len"), hmac = Field(parts[5], "hmac");
        if (v != "1") throw new SaveMalformedException("unsupported container version");
        if (fmt != "json") throw new SaveMalformedException("unsupported fmt");
        if (crcHex.Length != 8 || !uint.TryParse(crcHex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint crc))
            throw new SaveMalformedException("bad crc32 field");
        if (!int.TryParse(lenStr, NumberStyles.None, CultureInfo.InvariantCulture, out int len))
            throw new SaveMalformedException("bad len field");
        if (hmac != "-" && (hmac.Length != 64 || !IsHex(hmac))) throw new SaveMalformedException("bad hmac field");

        int start = nl + 1;
        int remaining = file.Length - start;
        if (remaining != len) throw new SaveMalformedException("body length does not match len");
        var body = file.AsSpan(start, len);
        if (Crc32.Compute(body) != crc) throw new SaveMalformedException("crc mismatch");

        var e = new SaveEnvelope(start, len, hmac.ToLowerInvariant(), SHA256.HashData(body));
        e.ReadBody(file.AsMemory(start, len));
        return e;
    }

    /// <summary>True when the header's HMAC is HMAC-SHA256 of the body under <paramref name="deviceKey"/> (02 section 10.1).</summary>
    public bool HmacValid(byte[] file, byte[]? deviceKey)
    {
        if (deviceKey == null || HmacHex == "-") return false;
        byte[] want = HMACSHA256.HashData(deviceKey, file.AsSpan(BodyOffset, BodyLength));
        return CryptographicOperations.FixedTimeEquals(want, Convert.FromHexString(HmacHex));
    }

    void ReadBody(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SaveMalformedException("body is not a JSON object");
            SchemaVersion = Required(root, "SchemaVersion").GetInt32();
            SaveSeq = Required(root, "SaveSeq").GetInt64();
            PlayerId = Required(root, "PlayerId").GetGuid();
            DeviceId = Required(root, "DeviceId").GetGuid();
            ContentVersion = Required(root, "ContentVersion").GetString() ?? "";
            LastSavedServerTime = Required(Required(root, "Time"), "LastSavedServerTime").GetInt64();
        }
        catch (JsonException) { throw new SaveMalformedException("body is not valid JSON"); }
        catch (FormatException) { throw new SaveMalformedException("a body root field has the wrong format"); }
        catch (InvalidOperationException) { throw new SaveMalformedException("a body root field has the wrong type"); }
    }

    static JsonElement Required(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var p) ? p : throw new SaveMalformedException($"body has no {name}");

    static string Field(string part, string key) =>
        part.StartsWith(key + "=", StringComparison.Ordinal) ? part[(key.Length + 1)..] : throw new SaveMalformedException($"header field {key} missing");

    static bool IsHex(string s)
    {
        foreach (char c in s)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>Builds a valid file around a body (tests, fixtures): the inverse of <see cref="Parse"/>.</summary>
    public static byte[] Encode(string bodyJson, byte[]? deviceKey)
    {
        byte[] body = new UTF8Encoding(false).GetBytes(bodyJson);
        string hmac = deviceKey == null ? "-" : Convert.ToHexString(HMACSHA256.HashData(deviceKey, body)).ToLowerInvariant();
        byte[] header = Encoding.ASCII.GetBytes(
            $"{Magic} v=1 fmt=json crc32={Crc32.Compute(body):x8} len={body.Length.ToString(CultureInfo.InvariantCulture)} hmac={hmac}\n");
        return [.. header, .. body];
    }
}

/// <summary>CRC-32 (IEEE 802.3, reflected, polynomial 0xEDB88320), the save header's <c>crc32</c>.</summary>
public static class Crc32
{
    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
