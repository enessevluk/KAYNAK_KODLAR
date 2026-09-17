using System.Buffers.Binary;
using System.Security.Cryptography;

static class AdminTotp
{
    public static bool Verify(string? base32Secret, string? code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(base32Secret)) return true;
        var normalizedCode=new string((code??"").Where(char.IsDigit).ToArray());
        if (normalizedCode.Length!=6) return false;
        byte[] key;
        try { key=DecodeBase32(base32Secret); } catch { return false; }
        if (key.Length<10) return false;
        var step=now.ToUnixTimeSeconds()/30;
        for (var drift=-1;drift<=1;drift++)
        {
            var expected=Generate(key,step+drift);
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expected),
                System.Text.Encoding.ASCII.GetBytes(normalizedCode))) return true;
        }
        return false;
    }

    private static string Generate(byte[] key,long counter)
    {
        Span<byte> data=stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(data,counter);
        using var hmac=new HMACSHA1(key);
        var hash=hmac.ComputeHash(data.ToArray());
        var offset=hash[^1]&0x0f;
        var binary=((hash[offset]&0x7f)<<24)|((hash[offset+1]&0xff)<<16)|((hash[offset+2]&0xff)<<8)|(hash[offset+3]&0xff);
        return (binary%1_000_000).ToString("D6",System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] DecodeBase32(string value)
    {
        var clean=new string(value.ToUpperInvariant().Where(c=>c!=' '&&c!='-'&&c!='=').ToArray());
        var output=new List<byte>(); var buffer=0; var bits=0;
        foreach(var ch in clean)
        {
            var index=ch switch { >= 'A' and <= 'Z' => ch-'A', >= '2' and <= '7' => ch-'2'+26, _ => throw new FormatException("Invalid Base32") };
            buffer=(buffer<<5)|index; bits+=5;
            if(bits<8) continue;
            bits-=8; output.Add((byte)((buffer>>bits)&0xff));
        }
        return output.ToArray();
    }
}
