using System.Buffers.Binary;
using System.Numerics;

namespace ZteModemGui.Models;

// Native equivalents of zte_payload.py; retain little-endian VM operands.
public static class ProofPayload
{
    private const string Alphabet = "lmaoztebcdfghijknpqrsuvwxy";
    private static readonly Lazy<Dictionary<int, uint>> Header = new(() => EncodingMap(0x1687, 0x7561, false));
    private static readonly Lazy<Dictionary<int, uint>> MacWords = new(() => EncodingMap(1, 0x1687, true));

    public static byte[] ParseMac(string text)
    {
        try
        {
            var bytes = Convert.FromHexString(text.Replace(":", "").Replace("-", "").Replace(".", ""));
            if (bytes.Length == 6) return bytes;
        }
        catch (FormatException) { }
        throw new ArgumentException("Enter a six-byte MAC such as 00:07:29:55:35:57.");
    }

    public static byte[] Create(int method, byte[] bridge, byte[] client, string profile)
    {
        if (client.Length != 6 || (method == 3 && bridge.Length != 6))
            throw new ArgumentException("The handshake requires six-byte client and bridge MACs.");
        if (method == 2)
        {
            var reverse = Enumerable.Repeat(-1, 256).ToArray();
            for (int i = 0; i < 0x9E9; i++)
            {
                int decoded = (int)BigInteger.ModPow(i, 0x4F7, 0x9E9) & 255;
                if (reverse[decoded] < 0) reverse[decoded] = i;
            }
            var payload = new byte[46]; // Last historical operand is only two bytes.
            for (int i = 0; i < 6; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i * 4), (uint)reverse[client[i]]);
            return payload;
        }
        IEnumerable<uint> words = profile == "rerand34"
            ? new uint[] { 0, 1, 0, 9893 }.Concat(bridge.Concat(client).Concat(client)
                .Concat(Convert.FromHexString("00FF7246341100FF72463411")).Select(b => (uint)b))
            : new[] { Header.Value[0], Header.Value[1], Header.Value[0], Header.Value[0x1687] }
                .Concat(bridge.Concat(client).Concat(client).Select(b => MacWords.Value[b]));
        var array = words.ToArray();
        var result = new byte[array.Length * 4];
        for (int i = 0; i < array.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), array[i]);
        return result;
    }

    private static Dictionary<int, uint> EncodingMap(int exponent, int modulus, bool lowByte)
    {
        int Decode(uint word) => (int)BigInteger.ModPow(word, exponent, modulus) & (lowByte ? 255 : 0xFFFF);
        var required = Enumerable.Range(0, modulus).Select(i => Decode((uint)i)).ToHashSet();
        var map = new Dictionary<int, uint>();
        foreach (char a in Alphabet)
        foreach (char b in Alphabet)
        foreach (char c in Alphabet)
        foreach (char d in Alphabet)
        {
            uint word = (uint)a | (uint)b << 8 | (uint)c << 16 | (uint)d << 24;
            map.TryAdd(Decode(word), word);
            if (map.Count == required.Count) return map;
        }
        throw new InvalidOperationException("Cannot encode the MAC proof.");
    }
}
