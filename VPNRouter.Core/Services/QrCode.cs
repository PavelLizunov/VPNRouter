using System.Text;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.32.0 (Android-led, 2026-05-07) — pure-C# QR Code encoder.
///
/// <para>Adapted from Project Nayuki's QR Code generator library
/// (MIT license, <see href="https://www.nayuki.io/page/qr-code-generator-library"/>).
/// Trimmed to byte-mode encoding only — that covers UTF-8 strings of
/// arbitrary length (vless URI / subscription URL / arbitrary JSON
/// blob), which is all this app needs. Numeric / alphanumeric / kanji
/// modes are removed because we never produce all-digit QR strings.</para>
///
/// <para>No external dependencies, no native binaries — pure C#. Returns
/// a 2D matrix of booleans (<c>true</c> = dark module). Caller is
/// responsible for rendering — we don't pull in System.Drawing /
/// SkiaSharp / Avalonia from Core, so the rendering layer (Android
/// SkiaImaging or desktop SkiaSharp Bitmap) lives platform-side.</para>
///
/// <para><b>Why vendor instead of NuGet?</b> Available NuGets either pull
/// in System.Drawing.Common (broken on Android) or only target a single
/// rendering backend. ~600 LOC of well-known reference code is cheaper
/// than a bind layer + version pin.</para>
/// </summary>
public sealed class QrCode
{
    /// <summary>QR error correction level. Higher = bigger but more robust.</summary>
    public enum Ecc
    {
        Low = 0,      // ~7%  recoverable
        Medium = 1,   // ~15% recoverable — good default
        Quartile = 2, // ~25%
        High = 3,     // ~30%
    }

    public int Version { get; }
    public Ecc ErrorCorrection { get; }
    public int Size { get; }
    public int Mask { get; }
    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    /// <summary>True when the module at (x,y) is dark.</summary>
    public bool GetModule(int x, int y) =>
        x >= 0 && x < Size && y >= 0 && y < Size && _modules[y, x];

    /// <summary>Encode UTF-8 string to a QR matrix at the given correction level.</summary>
    public static QrCode EncodeText(string text, Ecc ecl)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var bytes = Encoding.UTF8.GetBytes(text);
        var seg = QrSegment.MakeBytes(bytes);
        return EncodeSegment(seg, ecl);
    }

    /// <summary>Render to a 2D bool matrix (true = dark module).</summary>
    public bool[,] ToMatrix()
    {
        var m = new bool[Size, Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                m[y, x] = _modules[y, x];
        return m;
    }

    // ── Internals ──────────────────────────────────────────────────────

    private const int MinVersion = 1;
    private const int MaxVersion = 40;

    private static QrCode EncodeSegment(QrSegment seg, Ecc ecl)
    {
        // Find smallest QR version that fits.
        int version, dataUsedBits;
        for (version = MinVersion; ; version++)
        {
            int dataCapacityBits = GetNumDataCodewords(version, ecl) * 8;
            dataUsedBits = QrSegment.GetTotalBits(seg, version);
            if (dataUsedBits != -1 && dataUsedBits <= dataCapacityBits) break;
            if (version >= MaxVersion)
                throw new InvalidOperationException("QR data too large for any version");
        }

        // Try to upgrade ECC level for free if data still fits.
        foreach (var newEcl in new[] { Ecc.Medium, Ecc.Quartile, Ecc.High })
        {
            if (newEcl > ecl && dataUsedBits <= GetNumDataCodewords(version, newEcl) * 8)
                ecl = newEcl;
        }

        // Concatenate all segments into a bit buffer.
        var bb = new BitBuffer();
        bb.AppendBits(seg.Mode, 4);
        bb.AppendBits(seg.NumChars, QrSegment.GetCharCountBitWidth(version));
        bb.AppendData(seg.Data);

        int dataCapacityBits2 = GetNumDataCodewords(version, ecl) * 8;
        bb.AppendBits(0, Math.Min(4, dataCapacityBits2 - bb.Length));
        bb.AppendBits(0, (8 - bb.Length % 8) % 8);

        for (int padByte = 0xEC; bb.Length < dataCapacityBits2; padByte ^= 0xEC ^ 0x11)
            bb.AppendBits(padByte, 8);

        var dataCodewords = new byte[bb.Length / 8];
        for (int i = 0; i < bb.Length; i++)
        {
            if (bb.GetBit(i))
                dataCodewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        }

        return new QrCode(version, ecl, dataCodewords, mask: -1);
    }

    private QrCode(int version, Ecc ecl, byte[] dataCodewords, int mask)
    {
        if (version < MinVersion || version > MaxVersion)
            throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
        ErrorCorrection = ecl;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];

        DrawFunctionPatterns();
        var allCodewords = AddEccAndInterleave(dataCodewords);
        DrawCodewords(allCodewords);

        // Pick best mask.
        if (mask == -1)
        {
            int minPenalty = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                ApplyMask(i);
                DrawFormatBits(i);
                int penalty = GetPenaltyScore();
                if (penalty < minPenalty)
                {
                    mask = i;
                    minPenalty = penalty;
                }
                ApplyMask(i); // undo
            }
        }
        ApplyMask(mask);
        DrawFormatBits(mask);
        Mask = mask;
    }

    // ─── Function patterns ─────────────────────────────────────────────

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            SetFunctionModule(6, i, i % 2 == 0);
            SetFunctionModule(i, 6, i % 2 == 0);
        }

        DrawFinderPattern(3, 3);
        DrawFinderPattern(Size - 4, 3);
        DrawFinderPattern(3, Size - 4);

        var alignPatPos = GetAlignmentPatternPositions();
        int numAlign = alignPatPos.Length;
        for (int i = 0; i < numAlign; i++)
            for (int j = 0; j < numAlign; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == numAlign - 1) || (i == numAlign - 1 && j == 0))
                    continue;
                DrawAlignmentPattern(alignPatPos[i], alignPatPos[j]);
            }

        DrawFormatBits(0);
        DrawVersion();
    }

    private void DrawFormatBits(int mask)
    {
        int data = (int)ErrorCorrection << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++)
            SetFunctionModule(8, i, GetBit(bits, i));
        SetFunctionModule(8, 7, GetBit(bits, 6));
        SetFunctionModule(8, 8, GetBit(bits, 7));
        SetFunctionModule(7, 8, GetBit(bits, 8));
        for (int i = 9; i < 15; i++)
            SetFunctionModule(14 - i, 8, GetBit(bits, i));

        for (int i = 0; i < 8; i++)
            SetFunctionModule(Size - 1 - i, 8, GetBit(bits, i));
        for (int i = 8; i < 15; i++)
            SetFunctionModule(8, Size - 15 + i, GetBit(bits, i));
        SetFunctionModule(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        int rem = Version;
        for (int i = 0; i < 12; i++)
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        int bits = Version << 12 | rem;

        for (int i = 0; i < 18; i++)
        {
            bool bit = GetBit(bits, i);
            int a = Size - 11 + i % 3;
            int b = i / 3;
            SetFunctionModule(a, b, bit);
            SetFunctionModule(b, a, bit);
        }
    }

    private void DrawFinderPattern(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx;
                int yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                    SetFunctionModule(xx, yy, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignmentPattern(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunctionModule(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void SetFunctionModule(int x, int y, bool isDark)
    {
        _modules[y, x] = isDark;
        _isFunction[y, x] = true;
    }

    // ─── Codewords / ECC / interleave ──────────────────────────────────

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int ver = Version;
        int ecl = (int)ErrorCorrection;
        if (data.Length != GetNumDataCodewords(ver, ErrorCorrection))
            throw new ArgumentException("invalid data length", nameof(data));

        int numBlocks = NumErrorCorrectionBlocks[ecl, ver];
        int blockEccLen = EccCodewordsPerBlock[ecl, ver];
        int rawCodewords = GetNumRawDataModules(ver) / 8;
        int numShortBlocks = numBlocks - rawCodewords % numBlocks;
        int shortBlockLen = rawCodewords / numBlocks;

        var blocks = new byte[numBlocks][];
        var rsDiv = ReedSolomonComputeDivisor(blockEccLen);
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int dataLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = new byte[dataLen];
            Array.Copy(data, k, dat, 0, dataLen);
            k += dataLen;

            var block = new byte[shortBlockLen + 1];
            Array.Copy(dat, 0, block, 0, dat.Length);
            var ecc = ReedSolomonComputeRemainder(dat, rsDiv);
            Array.Copy(ecc, 0, block, block.Length - blockEccLen, ecc.Length);
            blocks[i] = block;
        }

        var result = new byte[rawCodewords];
        for (int i = 0, k = 0; i < blocks[0].Length; i++)
        {
            for (int j = 0; j < blocks.Length; j++)
            {
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    result[k++] = blocks[j][i];
            }
        }
        return result;
    }

    private void DrawCodewords(byte[] data)
    {
        // Per Nayuki's spec: rawDataModules / 8 (integer division) is
        // the codeword count. Any leftover bits (rawDataModules % 8) are
        // unwritten by the data loop and remain at the bottom-left
        // function-pattern row — masked along with the rest below.
        int rawCodewords = GetNumRawDataModules(Version) / 8;
        if (data.Length != rawCodewords)
            throw new ArgumentException("invalid data length", nameof(data));

        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < Size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = GetBit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
            }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    _ => false,
                };
                if (!_isFunction[y, x] && invert)
                    _modules[y, x] ^= true;
            }
    }

    private int GetPenaltyScore()
    {
        int result = 0;

        // Adjacent runs of 5+ same-coloured modules.
        for (int y = 0; y < Size; y++)
        {
            bool runColor = false;
            int runX = 0;
            int[] runHistory = new int[7];
            for (int x = 0; x < Size; x++)
            {
                if (_modules[y, x] == runColor)
                {
                    runX++;
                    if (runX == 5) result += 3;
                    else if (runX > 5) result++;
                }
                else
                {
                    FinderPenaltyAddHistory(runX, runHistory);
                    if (!runColor) result += FinderPenaltyCountPatterns(runHistory) * 40;
                    runColor = _modules[y, x];
                    runX = 1;
                }
            }
            result += FinderPenaltyTerminateAndCount(runColor, runX, runHistory) * 40;
        }
        for (int x = 0; x < Size; x++)
        {
            bool runColor = false;
            int runY = 0;
            int[] runHistory = new int[7];
            for (int y = 0; y < Size; y++)
            {
                if (_modules[y, x] == runColor)
                {
                    runY++;
                    if (runY == 5) result += 3;
                    else if (runY > 5) result++;
                }
                else
                {
                    FinderPenaltyAddHistory(runY, runHistory);
                    if (!runColor) result += FinderPenaltyCountPatterns(runHistory) * 40;
                    runColor = _modules[y, x];
                    runY = 1;
                }
            }
            result += FinderPenaltyTerminateAndCount(runColor, runY, runHistory) * 40;
        }

        // 2x2 same-coloured blocks.
        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
            {
                bool color = _modules[y, x];
                if (color == _modules[y, x + 1] && color == _modules[y + 1, x] && color == _modules[y + 1, x + 1])
                    result += 3;
            }

        // Black/white ratio.
        int dark = 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                if (_modules[y, x]) dark++;
        int total = Size * Size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += k * 10;

        return result;
    }

    private int FinderPenaltyCountPatterns(int[] runHistory)
    {
        int n = runHistory[1];
        bool core = n > 0 && runHistory[2] == n && runHistory[3] == n * 3 && runHistory[4] == n && runHistory[5] == n;
        return (core && runHistory[0] >= n * 4 && runHistory[6] >= n ? 1 : 0)
             + (core && runHistory[6] >= n * 4 && runHistory[0] >= n ? 1 : 0);
    }

    private int FinderPenaltyTerminateAndCount(bool currentRunColor, int currentRunLength, int[] runHistory)
    {
        if (currentRunColor)
        {
            FinderPenaltyAddHistory(currentRunLength, runHistory);
            currentRunLength = 0;
        }
        currentRunLength += Size;
        FinderPenaltyAddHistory(currentRunLength, runHistory);
        return FinderPenaltyCountPatterns(runHistory);
    }

    private void FinderPenaltyAddHistory(int currentRunLength, int[] runHistory)
    {
        if (runHistory[0] == 0) currentRunLength += Size;
        Array.Copy(runHistory, 0, runHistory, 1, runHistory.Length - 1);
        runHistory[0] = currentRunLength;
    }

    // ─── Tables / utility ──────────────────────────────────────────────

    private int[] GetAlignmentPatternPositions()
    {
        if (Version == 1) return Array.Empty<int>();
        int numAlign = Version / 7 + 2;
        int step = Version == 32 ? 26 : (Version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = result.Length - 1, pos = Size - 7; i >= 1; i--, pos -= step)
            result[i] = pos;
        return result;
    }

    private static int GetNumRawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int GetNumDataCodewords(int ver, Ecc ecl)
    {
        return GetNumRawDataModules(ver) / 8
             - EccCodewordsPerBlock[(int)ecl, ver]
             * NumErrorCorrectionBlocks[(int)ecl, ver];
    }

    private static byte[] ReedSolomonComputeDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < result.Length; j++)
            {
                result[j] = (byte)ReedSolomonMultiply(result[j] & 0xFF, root);
                if (j + 1 < result.Length)
                    result[j] ^= result[j + 1];
            }
            root = ReedSolomonMultiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = (b ^ result[0]) & 0xFF;
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[result.Length - 1] = 0;
            for (int i = 0; i < result.Length; i++)
                result[i] ^= (byte)ReedSolomonMultiply(divisor[i] & 0xFF, factor);
        }
        return result;
    }

    private static int ReedSolomonMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    private static bool GetBit(int x, int i) => ((x >> i) & 1) != 0;

    // Reed-Solomon ECC tables — Nayuki reference (rows: L M Q H, cols: ver 0..40)
    private static readonly sbyte[,] EccCodewordsPerBlock =
    {
        {-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30},
        {-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28},
        {-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30},
        {-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30},
    };

    private static readonly sbyte[,] NumErrorCorrectionBlocks =
    {
        {-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25},
        {-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49},
        {-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68},
        {-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81},
    };

    // ─── Bit buffer + segment helpers (byte-mode only) ─────────────────

    private sealed class QrSegment
    {
        public int Mode { get; }
        public int NumChars { get; }
        public BitBuffer Data { get; }

        private QrSegment(int mode, int numChars, BitBuffer data)
        {
            Mode = mode;
            NumChars = numChars;
            Data = data;
        }

        // Byte mode: 4-bit indicator 0100, then 8-bit char count up to v9,
        // 16-bit on v10+. Char count bit width returned by GetCharCountBitWidth.
        public static QrSegment MakeBytes(byte[] data)
        {
            var bb = new BitBuffer();
            foreach (var b in data) bb.AppendBits(b, 8);
            return new QrSegment(0x4, data.Length, bb);
        }

        public static int GetCharCountBitWidth(int version)
        {
            if (version < 1 || version > 40) throw new ArgumentOutOfRangeException(nameof(version));
            // Byte mode counts.
            return version < 10 ? 8 : 16;
        }

        public static int GetTotalBits(QrSegment seg, int version)
        {
            int ccBits = GetCharCountBitWidth(version);
            if (seg.NumChars >= (1 << ccBits)) return -1;
            return 4 + ccBits + seg.Data.Length;
        }
    }

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = new();

        public int Length => _bits.Count;

        public bool GetBit(int index) => _bits[index];

        public void AppendBits(int value, int len)
        {
            if (len < 0 || len > 31)
                throw new ArgumentOutOfRangeException(nameof(len));
            if (len < 31 && (value < 0 || value >= (1 << len)))
                throw new ArgumentOutOfRangeException(nameof(value));
            for (int i = len - 1; i >= 0; i--)
                _bits.Add(((value >> i) & 1) != 0);
        }

        public void AppendData(BitBuffer other)
        {
            _bits.AddRange(other._bits);
        }
    }
}
