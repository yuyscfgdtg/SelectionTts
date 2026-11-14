using System.Text;

namespace SelectionTts.Tts;

/// <summary>
/// Edge 朗读接口的 Sec-MS-GEC v2 令牌。
///
/// 与 Python edge-tts 的 drm.py 等价：令牌格式为 "CS64(16 hex)-SHA256(64 hex)"，
/// 其中 SHA-256 使用了修改过的常量表。这个算法随 Edge 版本变化，
/// 升级时要同步 constants.py 里的版本号和这里的常量。
/// </summary>
internal static class DrmToken
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    public const string ChromiumVersion = "143.0.3650.75";
    public const string SecMsGecVersion = "2-" + ChromiumVersion;

    private const long WindowsEpochSeconds = 11644473600;
    private const uint Mask = 0xFFFFFFFF;

    private static readonly uint[] InitialState =
    [
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
        0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    ];

    private static readonly uint[] ResumeState =
    [
        0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A,
        0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2,
    ];

    private static readonly uint[] ScheduleConstants =
    [
        0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C,
        0xD1DD9491, 0xC0DD25A0, 0xF9E70AB6, 0xF0C31573, 0x7058CE4E,
        0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564,
    ];

    private static readonly uint[] RoundConstants =
    [
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    ];

    private const uint ShaAdjustment = 0xC9DC06A7;
    private const uint Sigma0Adjustment = 0x059C0B80;
    private const uint Sigma1Adjustment = 0x000B92C0;

    private static readonly uint[] BlockConstants =
        RoundConstants.Take(14).Append(ShaAdjustment).Concat(RoundConstants.Skip(15)).ToArray();

    private static readonly (uint Coefficient, uint Offset)[] Cs64Coefficients =
    [
        (0xE82E91B1, 0xB32700D0),
        (0x44DE24C6, 0x88DFF51D),
        (0x3BEB65BC, 0x29514038),
        (0xC89C5933, 0xB328CBDF),
        (0x8D1EC211, 0xD405B4D5),
        (0x314EF5D5, 0x26E55F3C),
        (0xE92491B1, 0x607801C6),
        (0xF0A5B9D5, 0x82B2C1FB),
        (0x06064CF3, 0xFA51A3DA),
        (0xDA79FA60, 0x207CC464),
        (0xBED20560, 0x3C3C74F3),
        (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1),
        (0xCB49E3C6, 0xC5626167),
    ];

    private const uint Cs64Factor = 0x82893933;
    private const uint Cs64Adjustment0 = 0xF2F90B1A;
    private const uint Cs64Adjustment1 = 0x0B1A0000;

    public static string Generate(long? unixTimestamp = null)
    {
        var seconds = (unixTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + WindowsEpochSeconds;
        seconds -= seconds % 300;
        var ticks = seconds * 10_000_000;

        var data = Encoding.ASCII.GetBytes($"{ticks}{TrustedClientToken}");
        var digest = Sha256Digest(data);
        var (swapLast, swapAcc) = Cs64Digest(data, digest);

        // struct.pack("<II", a, b) 是 little-endian，十六进制要按字节顺序展开
        var cs64 = LeHex(swapLast) + LeHex(swapAcc);
        return cs64 + "-" + Convert.ToHexString(digest);
    }

    private static string LeHex(uint value) =>
        $"{(byte)value:x2}{(byte)(value >> 8):x2}{(byte)(value >> 16):x2}{(byte)(value >> 24):x2}"
            .ToUpperInvariant();

    private static uint Rol16(uint value) => (value << 16) | (value >> 16);

    private static uint Sigma0(uint value) =>
        (value >> 7) ^ (value << 25) ^ (value >> 18) ^ (value << 14) ^ (value >> 3);

    private static uint Sigma1(uint value) =>
        (value >> 17) ^ (value << 15) ^ (value >> 19) ^ (value << 13) ^ (value >> 10);

    private static uint Rotr(uint value, int bits) => (value >> bits) | (value << (32 - bits));

    private static uint BigSigma1(uint value) => Rotr(value, 6) ^ Rotr(value, 11) ^ Rotr(value, 25);

    private static uint BigSigma0(uint value) => Rotr(value, 2) ^ Rotr(value, 13) ^ Rotr(value, 22);

    private static uint Choose(uint e, uint f, uint g) => g ^ (e & (f ^ g));

    private static uint Majority(uint a, uint b, uint c) => (a & b) | (c & (a | b));

    private static uint[] Rounds(uint[] state, uint[] words, int firstRound)
    {
        uint a = state[0], b = state[1], c = state[2], d = state[3];
        uint e = state[4], f = state[5], g = state[6], h = state[7];

        var constants = firstRound == 14 ? BlockConstants : RoundConstants;
        for (var i = firstRound; i < 64; i++)
        {
            var t1 = h + BigSigma1(e) + Choose(e, f, g) + constants[i] + words[i];
            var t2 = BigSigma0(a) + Majority(a, b, c);

            h = g;
            g = f;
            f = e;
            e = d + t1;
            d = c;
            c = b;
            b = a;
            a = t1 + t2;
        }

        return [a, b, c, d, e, f, g, h];
    }

    private static void ExpandSchedule(uint[] words, int firstWord)
    {
        for (var i = firstWord; i < 64; i++)
        {
            words[i] = words[i - 16] + Sigma0(words[i - 15]) + words[i - 7] + Sigma1(words[i - 2]);
        }
    }

    private static uint[] Sha256Init(ReadOnlySpan<byte> firstSeven)
    {
        var words = new uint[64];
        words[14] = (uint)((firstSeven[0] << 16) | (firstSeven[1] << 8) | firstSeven[2]);
        words[15] = (uint)((firstSeven[3] << 24) | (firstSeven[4] << 16) | (firstSeven[5] << 8) | firstSeven[6]);

        for (var index = 16; index < 30; index++)
        {
            var value = ScheduleConstants[index - 16];

            value += index == 16
                ? Sigma1(words[index - 2]) ^ Sigma1Adjustment
                : Sigma1(words[index - 2]);

            if (index - 7 >= 14)
            {
                value += words[index - 7];
            }

            if (index - 15 >= 14)
            {
                value += index == 29
                    ? Sigma0(words[index - 15]) ^ Sigma0Adjustment
                    : Sigma0(words[index - 15]);
            }

            words[index] = value;
        }

        words[30] = Sigma1(words[28]) + words[23] + Sigma0(words[15]) + words[14]
                    + ShaAdjustment - RoundConstants[14];

        ExpandSchedule(words, 31);

        var working = Rounds(ResumeState, words, 14);
        var state = new uint[8];
        for (var i = 0; i < 8; i++)
        {
            state[i] = InitialState[i] + working[i];
        }

        return state;
    }

    private static byte[] Sha256Digest(byte[] data)
    {
        if (data.Length < 7)
        {
            throw new ArgumentException("token data must be at least 7 bytes", nameof(data));
        }

        var length = 57 + data.Length;
        var padding = (55 - length) % 64;
        if (padding < 0)
        {
            padding += 64;
        }

        var padded = new List<byte>(data.Length + padding + 8);
        padded.AddRange(data.Skip(7));
        padded.Add(0x80);
        padded.AddRange(new byte[padding]);

        var bitLength = (ulong)(length * 8);
        for (var shift = 56; shift >= 0; shift -= 8)
        {
            padded.Add((byte)(bitLength >> shift));
        }

        var state = Sha256Init(data.AsSpan(0, 7));
        var words = new uint[64];

        for (var offset = 0; offset < padded.Count; offset += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                var p = offset + i * 4;
                words[i] = (uint)((padded[p] << 24) | (padded[p + 1] << 16) | (padded[p + 2] << 8) | padded[p + 3]);
            }

            ExpandSchedule(words, 16);
            var working = Rounds(state, words, 0);
            for (var i = 0; i < 8; i++)
            {
                state[i] += working[i];
            }
        }

        var digest = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            digest[i * 4] = (byte)(state[i] >> 24);
            digest[i * 4 + 1] = (byte)(state[i] >> 16);
            digest[i * 4 + 2] = (byte)(state[i] >> 8);
            digest[i * 4 + 3] = (byte)state[i];
        }

        return digest;
    }

    private static (uint SwapLast, uint SwapAcc) Cs64Digest(byte[] data, byte[] digest)
    {
        var keyA = BitConverter.ToUInt32(digest, 0) | 1;
        var keyB = BitConverter.ToUInt32(digest, 4) | 1;

        var factors = new[]
        {
            keyA * Cs64Factor,
            keyB * Cs64Factor,
        };

        var terms = new List<uint>(Cs64Coefficients.Length + 16);
        var offsets = new List<uint>(Cs64Coefficients.Length + 16);

        for (var i = 0; i < Cs64Coefficients.Length; i++)
        {
            terms.Add(Cs64Coefficients[i].Coefficient * factors[i % 2]);
            offsets.Add(Cs64Coefficients[i].Offset);
        }

        var payload = new byte[data.Length + 1];
        payload[0] = 0;
        Array.Copy(data, 0, payload, 1, data.Length);

        foreach (var (word, index) in WordsLe4Even(payload).Select((word, index) => (word, index)))
        {
            var term = word * (index % 2 == 0 ? keyA : keyB);
            var offset = Rol16(word) * (index % 2 == 0 ? 0xEF0569FB : 0xC31713DB);

            if (index == 0)
            {
                term += Cs64Adjustment0 * factors[0];
                offset += Cs64Adjustment1;
            }

            terms.Add(term);
            offsets.Add(offset);
        }

        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
        for (var i = 0; i < terms.Count; i += 2)
        {
            var x = terms[i];
            var y = terms[i + 1];

            var t = Rol16(offsets[i] + x);
            var q = t * 0x76F50F34;
            swapAcc += Rol16(q) * 0xC3EFEA97 + q;

            t = Rol16(offsets[i + 1] + y);
            q = t * 0x1881CE3C;
            swapLast = Rol16(q) * 0x35BD1EC9 + q;
            swapAcc += swapLast;

            state = state * keyA + x;
            state = Rol16(state) * 0xCF98B111;
            state = Rol16(state) * 0x87085B9F;
            state = Rol16(state) * 0x12CEB96D;
            state = Rol16(state) * 0x257E1D83;
            acc += state;

            t = state * keyB + y;
            t = Rol16(t) * 0xA27416F5;
            t = Rol16(t) * 0xD38396FF;
            state = Rol16(Rol16(t) * 0x7C932B89) * 0xBFA49F69;
            acc += state;
        }

        return (swapLast ^ state, swapAcc ^ acc);
    }

    private static uint[] WordsLe4Even(byte[] data)
    {
        var paddedLength = (data.Length + 3) / 4 * 4;
        var padded = new byte[paddedLength];
        Array.Copy(data, padded, data.Length);

        var words = new uint[paddedLength / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BitConverter.ToUInt32(padded, i * 4);
        }

        if (words.Length % 2 != 0)
        {
            Array.Resize(ref words, words.Length + 1);
        }

        return words;
    }
}
