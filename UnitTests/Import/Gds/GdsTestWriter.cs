using System.Text;
using CAP_DataAccess.Import.Gds;

namespace UnitTests.Import.Gds;

/// <summary>
/// In-memory builder for valid GDSII record byte sequences. All tests build
/// their fixtures this way — no external files. Coordinates are passed in
/// database units (ints), exactly like a real GDS writer emits them.
/// </summary>
internal sealed class GdsTestWriter
{
    private readonly MemoryStream _stream = new();

    public static GdsTestWriter Create() => new();

    public byte[] ToArray() => _stream.ToArray();

    /// <summary>Writes one GDS record: 2-byte big-endian length + type + data type + payload.</summary>
    public GdsTestWriter WriteRecord(byte recordType, byte dataType, byte[] payload)
    {
        int length = payload.Length + 4;
        _stream.WriteByte((byte)((length >> 8) & 0xFF));
        _stream.WriteByte((byte)(length & 0xFF));
        _stream.WriteByte(recordType);
        _stream.WriteByte(dataType);
        _stream.Write(payload, 0, payload.Length);
        return this;
    }

    /// <summary>Writes raw bytes verbatim — for malformed-framing fixtures no helper can express.</summary>
    public GdsTestWriter WriteRawBytes(params byte[] bytes)
    {
        _stream.Write(bytes, 0, bytes.Length);
        return this;
    }

    // ── Library structure ────────────────────────────────────────────────────

    public GdsTestWriter Header() =>
        WriteRecord(GdsRecordTypes.Header, GdsDataTypes.Int2, Int2Bytes(600));

    public GdsTestWriter BeginLibrary() =>
        WriteRecord(GdsRecordTypes.BgnLib, GdsDataTypes.Int2, new byte[24]);

    public GdsTestWriter LibraryName(string name) =>
        WriteRecord(GdsRecordTypes.LibName, GdsDataTypes.AsciiString, StringBytes(name));

    public GdsTestWriter Units(double userUnitsPerDbUnit, double dbUnitInMeters) =>
        WriteRecord(GdsRecordTypes.Units, GdsDataTypes.Real8,
            EncodeReal8(userUnitsPerDbUnit).Concat(EncodeReal8(dbUnitInMeters)).ToArray());

    public GdsTestWriter BeginCell(string name) =>
        WriteRecord(GdsRecordTypes.BgnStr, GdsDataTypes.Int2, new byte[24])
            .WriteRecord(GdsRecordTypes.StrName, GdsDataTypes.AsciiString, StringBytes(name));

    public GdsTestWriter EndCell() =>
        WriteRecord(GdsRecordTypes.EndStr, GdsDataTypes.NoData, Array.Empty<byte>());

    public GdsTestWriter EndLibrary() =>
        WriteRecord(GdsRecordTypes.EndLib, GdsDataTypes.NoData, Array.Empty<byte>());

    /// <summary>Standard prologue: HEADER + BGNLIB + LIBNAME + UNITS with 1 db unit = 1 nm.</summary>
    public GdsTestWriter StandardPrologue(string libraryName = "testlib") =>
        Header().BeginLibrary().LibraryName(libraryName).Units(1e-3, 1e-9);

    // ── Elements ─────────────────────────────────────────────────────────────

    public GdsTestWriter Boundary(int layer, int dataType, params (int X, int Y)[] points) =>
        WriteRecord(GdsRecordTypes.Boundary, GdsDataTypes.NoData, Array.Empty<byte>())
            .WriteRecord(GdsRecordTypes.Layer, GdsDataTypes.Int2, Int2Bytes(layer))
            .WriteRecord(GdsRecordTypes.DataType, GdsDataTypes.Int2, Int2Bytes(dataType))
            .WriteRecord(GdsRecordTypes.XY, GdsDataTypes.Int4, XyBytes(points))
            .WriteRecord(GdsRecordTypes.EndEl, GdsDataTypes.NoData, Array.Empty<byte>());

    public GdsTestWriter Path(int layer, int dataType, int widthDbUnits, int pathType, params (int X, int Y)[] points) =>
        WriteRecord(GdsRecordTypes.Path, GdsDataTypes.NoData, Array.Empty<byte>())
            .WriteRecord(GdsRecordTypes.Layer, GdsDataTypes.Int2, Int2Bytes(layer))
            .WriteRecord(GdsRecordTypes.DataType, GdsDataTypes.Int2, Int2Bytes(dataType))
            .WriteRecord(GdsRecordTypes.PathType, GdsDataTypes.Int2, Int2Bytes(pathType))
            .WriteRecord(GdsRecordTypes.Width, GdsDataTypes.Int4, Int4Bytes(widthDbUnits))
            .WriteRecord(GdsRecordTypes.XY, GdsDataTypes.Int4, XyBytes(points))
            .WriteRecord(GdsRecordTypes.EndEl, GdsDataTypes.NoData, Array.Empty<byte>());

    public GdsTestWriter Text(int layer, int textType, string text, int x, int y, double? angleDegrees = null)
    {
        WriteRecord(GdsRecordTypes.Text, GdsDataTypes.NoData, Array.Empty<byte>())
            .WriteRecord(GdsRecordTypes.Layer, GdsDataTypes.Int2, Int2Bytes(layer))
            .WriteRecord(GdsRecordTypes.TextType, GdsDataTypes.Int2, Int2Bytes(textType));
        if (angleDegrees.HasValue)
            WriteRecord(GdsRecordTypes.Angle, GdsDataTypes.Real8, EncodeReal8(angleDegrees.Value));
        return WriteRecord(GdsRecordTypes.XY, GdsDataTypes.Int4, XyBytes((x, y)))
            .WriteRecord(GdsRecordTypes.String, GdsDataTypes.AsciiString, StringBytes(text))
            .WriteRecord(GdsRecordTypes.EndEl, GdsDataTypes.NoData, Array.Empty<byte>());
    }

    public GdsTestWriter SRef(
        string cellName, int x, int y,
        double? angleDegrees = null, double? magnification = null, bool reflected = false,
        bool magnificationAsReal4 = false)
    {
        WriteRecord(GdsRecordTypes.SRef, GdsDataTypes.NoData, Array.Empty<byte>())
            .WriteRecord(GdsRecordTypes.SName, GdsDataTypes.AsciiString, StringBytes(cellName));
        WriteOptionalTransform(angleDegrees, magnification, reflected, magnificationAsReal4);
        return WriteRecord(GdsRecordTypes.XY, GdsDataTypes.Int4, XyBytes((x, y)))
            .WriteRecord(GdsRecordTypes.EndEl, GdsDataTypes.NoData, Array.Empty<byte>());
    }

    /// <summary>
    /// Writes an AREF the way real writers emit it: the second and third XY
    /// points are the lattice endpoints — origin + count × spacing, rotated by
    /// the reference angle (and mirrored about X when reflected).
    /// </summary>
    public GdsTestWriter ARef(
        string cellName, int columns, int rows,
        int originX, int originY, int columnSpacingDbUnits, int rowSpacingDbUnits,
        double angleDegrees = 0.0, bool reflected = false, double? magnification = null)
    {
        WriteRecord(GdsRecordTypes.ARef, GdsDataTypes.NoData, Array.Empty<byte>())
            .WriteRecord(GdsRecordTypes.SName, GdsDataTypes.AsciiString, StringBytes(cellName));
        WriteOptionalTransform(angleDegrees, magnification, reflected, false);
        WriteRecord(GdsRecordTypes.ColRow, GdsDataTypes.Int2, Int2Bytes(columns, rows));

        double radians = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double ySign = reflected ? -1.0 : 1.0;
        int columnEndX = originX + (int)Math.Round(columns * columnSpacingDbUnits * cos);
        int columnEndY = originY + (int)Math.Round(columns * columnSpacingDbUnits * sin);
        int rowEndX = originX + (int)Math.Round(-rows * rowSpacingDbUnits * ySign * sin);
        int rowEndY = originY + (int)Math.Round(rows * rowSpacingDbUnits * ySign * cos);

        return WriteRecord(GdsRecordTypes.XY, GdsDataTypes.Int4,
                XyBytes((originX, originY), (columnEndX, columnEndY), (rowEndX, rowEndY)))
            .WriteRecord(GdsRecordTypes.EndEl, GdsDataTypes.NoData, Array.Empty<byte>());
    }

    // ── Raw value encoders ───────────────────────────────────────────────────

    /// <summary>
    /// Encodes a double as a GDS 8-byte real (sign, excess-64 exponent, 56-bit
    /// base-16 mantissa). Scaling by 16 is an exact exponent shift in binary
    /// floating point, so any double round-trips bit-exactly through the decoder.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value's exponent does not fit the 7-bit excess-64 field — the fixture
    /// is not representable as a GDS real.
    /// </exception>
    public static byte[] EncodeReal8(double value)
    {
        var bytes = new byte[8];
        if (value == 0.0)
            return bytes;

        bool negative = value < 0;
        double mantissaFraction = Math.Abs(value);
        int exponent = 64;
        while (mantissaFraction >= 1.0) { mantissaFraction /= 16.0; exponent++; }
        while (mantissaFraction < 0.0625) { mantissaFraction *= 16.0; exponent--; }
        GuardExponentRange(exponent, value);

        ulong mantissa = (ulong)(mantissaFraction * 72057594037927936.0 /* 2^56 */);
        bytes[0] = (byte)(exponent | (negative ? 0x80 : 0));
        for (int i = 6; i >= 0; i--)
        {
            bytes[i + 1] = (byte)(mantissa & 0xFF);
            mantissa >>= 8;
        }
        return bytes;
    }

    /// <summary>
    /// Encodes a double as a GDS 4-byte real. Only exact for values whose
    /// significand fits into 24 bits (small powers of two, small integers).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value's exponent does not fit the 7-bit excess-64 field — the fixture
    /// is not representable as a GDS real.
    /// </exception>
    public static byte[] EncodeReal4(double value)
    {
        var bytes = new byte[4];
        if (value == 0.0)
            return bytes;

        bool negative = value < 0;
        double mantissaFraction = Math.Abs(value);
        int exponent = 64;
        while (mantissaFraction >= 1.0) { mantissaFraction /= 16.0; exponent++; }
        while (mantissaFraction < 0.0625) { mantissaFraction *= 16.0; exponent--; }
        GuardExponentRange(exponent, value);

        uint mantissa = (uint)(mantissaFraction * 16777216.0 /* 2^24 */);
        bytes[0] = (byte)(exponent | (negative ? 0x80 : 0));
        bytes[1] = (byte)((mantissa >> 16) & 0xFF);
        bytes[2] = (byte)((mantissa >> 8) & 0xFF);
        bytes[3] = (byte)(mantissa & 0xFF);
        return bytes;
    }

    /// <summary>
    /// Guards the 7-bit excess-64 exponent field (0…127): an out-of-range
    /// exponent would silently truncate into a wrong sign/exponent byte, so a
    /// nonsense fixture fails loudly here instead of writing corrupt bytes.
    /// </summary>
    private static void GuardExponentRange(int exponent, double value)
    {
        if (exponent is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value,
                "Value is not representable as a GDS real (exponent outside the 7-bit excess-64 range) — fix the test fixture.");
        }
    }

    /// <summary>Big-endian signed 2-byte integers (values are truncated to 16 bits).</summary>
    public static byte[] Int2Bytes(params int[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)((values[i] >> 8) & 0xFF);
            bytes[i * 2 + 1] = (byte)(values[i] & 0xFF);
        }
        return bytes;
    }

    /// <summary>Big-endian signed 4-byte integers.</summary>
    public static byte[] Int4Bytes(params int[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 4] = (byte)((values[i] >> 24) & 0xFF);
            bytes[i * 4 + 1] = (byte)((values[i] >> 16) & 0xFF);
            bytes[i * 4 + 2] = (byte)((values[i] >> 8) & 0xFF);
            bytes[i * 4 + 3] = (byte)(values[i] & 0xFF);
        }
        return bytes;
    }

    /// <summary>ASCII bytes, NUL-padded to an even length as the format requires.</summary>
    public static byte[] StringBytes(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length % 2 != 0)
            Array.Resize(ref bytes, bytes.Length + 1);
        return bytes;
    }

    private static byte[] XyBytes(params (int X, int Y)[] points)
    {
        var values = new int[points.Length * 2];
        for (int i = 0; i < points.Length; i++)
        {
            values[i * 2] = points[i].X;
            values[i * 2 + 1] = points[i].Y;
        }
        return Int4Bytes(values);
    }

    private GdsTestWriter WriteOptionalTransform(
        double? angleDegrees, double? magnification, bool reflected, bool magnificationAsReal4)
    {
        if (reflected)
            WriteRecord(GdsRecordTypes.STrans, GdsDataTypes.BitArray, Int2Bytes(unchecked((int)0x8000)));
        if (magnification.HasValue)
        {
            WriteRecord(GdsRecordTypes.Mag,
                magnificationAsReal4 ? GdsDataTypes.Real4 : GdsDataTypes.Real8,
                magnificationAsReal4 ? EncodeReal4(magnification.Value) : EncodeReal8(magnification.Value));
        }
        if (angleDegrees.HasValue)
            WriteRecord(GdsRecordTypes.Angle, GdsDataTypes.Real8, EncodeReal8(angleDegrees.Value));
        return this;
    }
}
