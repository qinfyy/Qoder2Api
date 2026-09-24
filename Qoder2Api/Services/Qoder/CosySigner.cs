using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qoder2Api.Services.Qoder;

public record CosyCreds(
    string UserID,
    string AuthToken,
    string? Name = null,
    string? Email = null,
    string? MachineID = null
);

public static class CosySigner
{
    private static readonly RSA Rsa;

    static CosySigner()
    {
        Rsa = RSA.Create();
        Rsa.FromXmlString(QoderConstants.RSAPublicKeyXml);
    }

    public static Dictionary<string, string> BuildCosyHeaders(byte[] body, string requestUrl, CosyCreds creds)
    {
        if (string.IsNullOrWhiteSpace(creds.UserID))
            throw new ArgumentException("Qoder UserID cannot be empty.", nameof(creds));
        if (string.IsNullOrWhiteSpace(creds.AuthToken))
            throw new ArgumentException("Qoder AuthToken cannot be empty.", nameof(creds));

        string aesKey = Guid.NewGuid().ToString("N")[..16];
        byte[] aesKeyBytes = Encoding.UTF8.GetBytes(aesKey);

        // 加密 UserInfo
        var userInfoObj = new
        {
            uid = creds.UserID,
            security_oauth_token = creds.AuthToken,
            name = creds.Name ?? "",
            aid = "",
            email = creds.Email ?? ""
        };
        byte[] userInfoJsonBytes = JsonSerializer.SerializeToUtf8Bytes(userInfoObj);

        byte[] encryptedUserInfo;
        using (var aes = Aes.Create())
        {
            aes.Key = aesKeyBytes;
            aes.IV = aesKeyBytes;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            encryptedUserInfo = encryptor.TransformFinalBlock(userInfoJsonBytes, 0, userInfoJsonBytes.Length);
        }
        string infoB64 = Convert.ToBase64String(encryptedUserInfo);

        // 使用 RSA 加密 AES 密钥
        byte[] encryptedAesKey = Rsa.Encrypt(aesKeyBytes, RSAEncryptionPadding.Pkcs1);
        string cosyKey = Convert.ToBase64String(encryptedAesKey);

        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string requestId = Guid.NewGuid().ToString();

        var payloadObj = new
        {
            version = "v1",
            requestId = requestId,
            info = infoB64,
            cosyVersion = QoderConstants.IDEVersion,
            ideVersion = ""
        };
        string payloadB64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payloadObj));

        Uri uri = new(requestUrl);
        string sigPath = uri.AbsolutePath;
        if (sigPath.StartsWith("/algo"))
        {
            sigPath = sigPath[5..];
        }

        string bodyLatin1 = Encoding.Latin1.GetString(body);
        string sigInput = $"{payloadB64}\n{cosyKey}\n{timestamp}\n{bodyLatin1}\n{sigPath}";
        string sig = Convert.ToHexStringLower(MD5.HashData(Encoding.Latin1.GetBytes(sigInput)));

        string machineId = string.IsNullOrWhiteSpace(creds.MachineID) ? Guid.NewGuid().ToString() : creds.MachineID;
        string bodyHash = Convert.ToHexStringLower(MD5.HashData(body));
        string bodyLength = body.Length.ToString();

        return new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer COSY.{payloadB64}.{sig}",
            ["Cosy-Key"] = cosyKey,
            ["Cosy-User"] = creds.UserID,
            ["Cosy-Date"] = timestamp,
            ["Cosy-Version"] = QoderConstants.IDEVersion,
            ["Cosy-Machineid"] = machineId,
            ["Cosy-Machinetoken"] = machineId,
            ["Cosy-Machinetype"] = QoderConstants.MachineType,
            ["Cosy-Machineos"] = QoderConstants.MachineOS,
            ["Cosy-Clienttype"] = QoderConstants.ClientType,
            ["Cosy-Clientip"] = "127.0.0.1",
            ["Cosy-Bodyhash"] = bodyHash,
            ["Cosy-Bodylength"] = bodyLength,
            ["Cosy-Sigpath"] = sigPath,
            ["Cosy-Data-Policy"] = QoderConstants.DataPolicy,
            ["Cosy-Organization-Id"] = "",
            ["Cosy-Organization-Tags"] = "",
            ["Login-Version"] = QoderConstants.LoginVersion,
            ["X-Request-Id"] = Guid.NewGuid().ToString()
        };
    }
}
