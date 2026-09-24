using System.Text;

namespace reg.Services.Qoder;

/// <summary>
/// Implements Qoder's WAF-bypass body encoding and decoding.
/// </summary>
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

    /// <summary>
    /// Applies Qoder WAF-bypass encoding to plaintext bytes.
    /// </summary>
    public static byte[] EncodeBody(byte[] plaintext)
    {
        if (plaintext == null || plaintext.Length == 0)
        {
            return [];
        }

        // Step 1: standard base64 encode
        string base64Str = Convert.ToBase64String(plaintext);
        byte[] encoded = Encoding.ASCII.GetBytes(base64Str);
        int n = encoded.Length;
        if (n == 0) return [];

        // Step 2: rearrange - split into thirds, reorder [tail][mid][head]
        int a = n / 3;
        byte[] rearranged = new byte[n];
        
        // tail: last a chars -> rearranged[0 .. a-1]
        Buffer.BlockCopy(encoded, n - a, rearranged, 0, a);
        // middle: from a to n - a -> rearranged[a .. n - a - 1]
        Buffer.BlockCopy(encoded, a, rearranged, a, n - 2 * a);
        // head: first a chars -> rearranged[n - a .. n - 1]
        Buffer.BlockCopy(encoded, 0, rearranged, n - a, a);

        // Step 3: substitute each character through mapping table
        byte[] outBytes = new byte[n];
        for (int i = 0; i < n; i++)
        {
            byte c = rearranged[i];
            outBytes[i] = c < 128 ? S2C[c] : c;
        }

        return outBytes;
    }

    /// <summary>
    /// Reverses the WAF-bypass encoding (for debugging / verification).
    /// </summary>
    public static byte[] DecodeBody(byte[] encoded)
    {
        if (encoded == null || encoded.Length == 0)
        {
            return [];
        }

        int n = encoded.Length;
        byte[] sub = new byte[n];

        // Step 3 inverse: substitute back
        for (int i = 0; i < n; i++)
        {
            byte c = encoded[i];
            sub[i] = c < 128 ? C2S[c] : c;
        }

        // Step 2 inverse: rearranged = tail(a) + mid(n-2a) + head(a)
        // Recover encoded = head + mid + tail
        int a = n / 3;
        byte[] b64 = new byte[n];

        // head: sub[n-a .. n-1] -> b64[0 .. a-1]
        Buffer.BlockCopy(sub, n - a, b64, 0, a);
        // mid: sub[a .. n-a-1] -> b64[a .. n-a-1]
        Buffer.BlockCopy(sub, a, b64, a, n - 2 * a);
        // tail: sub[0 .. a-1] -> b64[n-a .. n-1]
        Buffer.BlockCopy(sub, 0, b64, n - a, a);

        // Step 1 inverse: base64 decode
        string b64Str = Encoding.ASCII.GetString(b64);
        return Convert.FromBase64String(b64Str);
    }
}
