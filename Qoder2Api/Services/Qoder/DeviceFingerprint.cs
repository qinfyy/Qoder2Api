using System.Security.Cryptography;

namespace Qoder2Api.Services.Qoder;

public sealed class DeviceFingerprint
{
    public required string MachineId { get; init; }
    public required string MachineToken { get; init; }
    public required string MachineType { get; init; }
    public required string MachineCode { get; init; }
}

public static class DeviceFingerprintFactory
{
    public static DeviceFingerprint Create() => new()
    {
        MachineId = Guid.NewGuid().ToString(),
        MachineToken = RandomToken(88),
        MachineType = RandomHex(20),
        MachineCode = RandomHex(18),
    };

    public static string CreateMachineId() => Guid.NewGuid().ToString();

    private static string RandomToken(int length)
    {
        byte[] raw = RandomNumberGenerator.GetBytes(length);
        string s = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        while (s.Length < length)
        {
            s += Convert.ToBase64String(RandomNumberGenerator.GetBytes(length))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        return s[..length];
    }

    private static string RandomHex(int length) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes((length + 1) / 2))[..length];
}
