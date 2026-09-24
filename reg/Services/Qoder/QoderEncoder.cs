using System.Text;

namespace reg.Services.Qoder;

public static class QoderEncoder
{
    private static readonly byte[] S2C = new byte[128];
    private static readonly byte[] C2S = new byte[128];

    static QoderEncoder()
    {
        for (int i = 0; i < 128; i++)
        {
            S2C[i] = (byte)i;
            C2S[i] = (byte)i;
        }

        for (int i = 0; i < 64; i++)
        {
            byte stdChar = (byte)QoderConstants.StdAlphabet[i];
            byte customChar = (byte)QoderConstants.CustomAlphabet[i];
            S2C[stdChar] = customChar;
            C2S[customChar] = stdChar;
        }

        S2C[(byte)'='] = (byte)'$';
        C2S[(byte)'$'] = (byte)'=';
    }

    public static byte[] EncodeBody(byte[] plaintext)
    {
        if (plaintext == null || plaintext.Length == 0)
        {
            return [];
        }

        string base64Str = Convert.ToBase64String(plaintext);
        byte[] encoded = Encoding.ASCII.GetBytes(base64Str);
        int n = encoded.Length;
        if (n == 0) return [];

        int a = n / 3;
        byte[] rearranged = new byte[n];
        
        Buffer.BlockCopy(encoded, n - a, rearranged, 0, a);
        Buffer.BlockCopy(encoded, a, rearranged, a, n - 2 * a);
        Buffer.BlockCopy(encoded, 0, rearranged, n - a, a);

        byte[] outBytes = new byte[n];
        for (int i = 0; i < n; i++)
        {
            byte c = rearranged[i];
            outBytes[i] = c < 128 ? S2C[c] : c;
        }

        return outBytes;
    }

    public static byte[] DecodeBody(byte[] encoded)
    {
        if (encoded == null || encoded.Length == 0)
        {
            return [];
        }

        int n = encoded.Length;
        byte[] sub = new byte[n];

        for (int i = 0; i < n; i++)
        {
            byte c = encoded[i];
            sub[i] = c < 128 ? C2S[c] : c;
        }

        int a = n / 3;
        byte[] b64 = new byte[n];

        Buffer.BlockCopy(sub, n - a, b64, 0, a);
        Buffer.BlockCopy(sub, a, b64, a, n - 2 * a);
        Buffer.BlockCopy(sub, 0, b64, n - a, a);

        string b64Str = Encoding.ASCII.GetString(b64);
        return Convert.FromBase64String(b64Str);
    }
}
