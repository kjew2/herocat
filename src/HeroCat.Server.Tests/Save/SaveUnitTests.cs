using System.Text;
using HeroCat.Server.Features.Save;
using HeroCat.Server.Pipeline;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Save;

/// <summary>The save container reader (specs/02 section 5.1) and the Save feature's pure helpers, without a database.</summary>
[TestFixture]
public sealed class SaveUnitTests
{
    static readonly Guid Player = Guid.Parse("6b1f2c40-9d1e-4a7b-8f3e-2c1a9b7d5e01");
    static readonly Guid Device = Guid.Parse("0f9e8d7c-6b5a-4433-9211-aabbccddeeff");
    static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    internal static string Body(Guid player, Guid device, long seq, long lastSaved = 1_793_667_200, string pad = "") =>
        $"{{\"SchemaVersion\":1,\"ContentVersion\":\"2026.11.03\",\"PlayerId\":\"{player}\",\"DeviceId\":\"{device}\",\"SaveSeq\":{seq}," +
        $"\"Time\":{{\"LastSavedServerTime\":{lastSaved}}},\"Pad\":\"{pad}\"}}";

    [Test]
    public void Crc32_is_the_IEEE_check_value() =>
        Assert.That(Crc32.Compute(Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xCBF43926u));

    [Test]
    public void A_valid_file_parses_with_its_root_fields_hash_and_hmac()
    {
        string body = Body(Player, Device, 1874);
        byte[] file = SaveEnvelope.Encode(body, Key);
        Assert.That(Encoding.ASCII.GetString(file).Split('\n')[0],
                    Does.Match(@"^HEROCAT-SAVE v=1 fmt=json crc32=[0-9a-f]{8} len=[0-9]+ hmac=[0-9a-f]{64}$"));

        var e = SaveEnvelope.Parse(file);
        Assert.That((e.SchemaVersion, e.PlayerId, e.DeviceId, e.SaveSeq, e.ContentVersion, e.LastSavedServerTime),
                    Is.EqualTo((1, Player, Device, 1874L, "2026.11.03", 1_793_667_200L)));
        Assert.That(e.Sha256, Is.EqualTo(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body))));
        Assert.That(e.HmacValid(file, Key), Is.True);
        Assert.That(e.HmacValid(file, new byte[32]), Is.False);
        Assert.That(e.HmacValid(file, null), Is.False);
    }

    [Test]
    public void A_file_without_a_key_has_hmac_dash_and_never_verifies()
    {
        byte[] file = SaveEnvelope.Encode(Body(Player, Device, 1), null);
        var e = SaveEnvelope.Parse(file);
        Assert.That(e.HmacHex, Is.EqualTo("-"));
        Assert.That(e.HmacValid(file, Key), Is.False);
    }

    static IEnumerable<TestCaseData> Malformed()
    {
        byte[] good = SaveEnvelope.Encode(Body(Player, Device, 1), Key);
        string text = Encoding.ASCII.GetString(good);
        string header = text.Split('\n')[0];
        yield return new TestCaseData(Array.Empty<byte>()).SetName("empty");
        yield return new TestCaseData(Encoding.ASCII.GetBytes("no header at all")).SetName("no header line");
        yield return new TestCaseData(Encoding.ASCII.GetBytes(text.Replace("HEROCAT-SAVE", "OTHER-SAVE"))).SetName("wrong magic");
        yield return new TestCaseData(Encoding.ASCII.GetBytes(text.Replace("v=1", "v=2"))).SetName("container v2");
        yield return new TestCaseData(Encoding.ASCII.GetBytes(text.Replace("fmt=json", "fmt=bin"))).SetName("fmt");
        yield return new TestCaseData(good.Take(good.Length - 1).ToArray()).SetName("truncated body");
        yield return new TestCaseData(good.Concat(new[] { (byte)' ' }).ToArray()).SetName("trailing bytes");
        var flipped = (byte[])good.Clone();
        flipped[^3] ^= 0x01;
        yield return new TestCaseData(flipped).SetName("crc mismatch");
        yield return new TestCaseData(Encoding.ASCII.GetBytes(header.Replace("hmac=", "hmac=zz") + text[header.Length..])).SetName("bad hmac field");
        yield return new TestCaseData(SaveEnvelope.Encode("[1,2]", Key)).SetName("body not an object");
        yield return new TestCaseData(SaveEnvelope.Encode("{\"SchemaVersion\":1", Key)).SetName("body not JSON");
        yield return new TestCaseData(SaveEnvelope.Encode("{\"SchemaVersion\":1,\"SaveSeq\":1}", Key)).SetName("root fields missing");
        yield return new TestCaseData(SaveEnvelope.Encode(Body(Player, Device, 1).Replace($"\"{Device}\"", "\"not-a-guid\""), Key)).SetName("DeviceId not a GUID");
        yield return new TestCaseData(SaveEnvelope.Encode(Body(Player, Device, 1).Replace("\"SaveSeq\":1", "\"SaveSeq\":\"1\""), Key)).SetName("SaveSeq a string");
    }

    [TestCaseSource(nameof(Malformed))]
    public void A_malformed_file_is_refused(byte[] file) =>
        Assert.Throws<SaveMalformedException>(() => SaveEnvelope.Parse(file));

    [Test]
    public void Blob_keys_and_etags_have_the_documented_shape()
    {
        Assert.That(SaveController.BlobKey(Player, 1875), Is.EqualTo("saves/6b1f2c40-9d1e-4a7b-8f3e-2c1a9b7d5e01/1875.sav"));
        Assert.That(SaveController.BlobUri("herocat-saves-prod", "saves/x/1.sav"), Is.EqualTo("s3://herocat-saves-prod/saves/x/1.sav"));
        string a = SaveController.NewEtag(1875), b = SaveController.NewEtag(1875);
        Assert.That(a, Does.Match("^W/\"1875-[0-9a-f]{8}\"$"));
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void The_sampler_takes_every_suspicious_upload_and_RESIM_SAMPLE_PCT_of_the_rest()
    {
        var info = new[] { new PlausibilityFlag(Player, SaveFlags.SEQ_REGRESSION, SaveFlags.Info) };
        var suspicious = new[] { new PlausibilityFlag(Player, SaveFlags.HMAC_INVALID, PlausibilityFlag.Suspicious) };
        Assert.That(new ResimSampler(1, () => 0.999).Sample(suspicious), Is.True);
        Assert.That(new ResimSampler(1, () => 0.999).Sample(info), Is.False);
        Assert.That(new ResimSampler(1, () => 0.005).Sample(Array.Empty<PlausibilityFlag>()), Is.True);
        Assert.That(new ResimSampler(1, () => 0.01).Sample(Array.Empty<PlausibilityFlag>()), Is.False);
    }
}
