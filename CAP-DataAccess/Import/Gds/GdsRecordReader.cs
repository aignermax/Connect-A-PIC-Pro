using System.Buffers.Binary;
using System.Text;

namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// GDSII stream format record type codes (the subset understood by <see cref="GdsReader"/>).
/// </summary>
internal static class GdsRecordTypes
{
    public const byte Header = 0x00;
    public const byte BgnLib = 0x01;
    public const byte LibName = 0x02;
    public const byte Units = 0x03;
    public const byte EndLib = 0x04;
    public const byte BgnStr = 0x05;
    public const byte StrName = 0x06;
    public const byte EndStr = 0x07;
    public const byte Boundary = 0x08;
    public const byte Path = 0x09;
    public const byte SRef = 0x0A;
    public const byte ARef = 0x0B;
    public const byte Text = 0x0C;
    public const byte Layer = 0x0D;
    public const byte DataType = 0x0E;
    public const byte Width = 0x0F;
    public const byte XY = 0x10;
    public const byte EndEl = 0x11;
    public const byte SName = 0x12;
    public const byte ColRow = 0x13;
    public const byte TextType = 0x16;
    public const byte String = 0x19;
    public const byte STrans = 0x1A;
    public const byte Mag = 0x1B;
    public const byte Angle = 0x1C;
    public const byte PathType = 0x21;
    public const byte Box = 0x2D;
    public const byte BoxType = 0x2E;
}

/// <summary>
/// GDSII stream format data type codes (second tag byte of a record).
/// </summary>
internal static class GdsDataTypes
{
    public const byte NoData = 0x00;
    public const byte BitArray = 0x01;
    public const byte Int2 = 0x02;
    public const byte Int4 = 0x03;
    public const byte Real4 = 0x04;
    public const byte Real8 = 0x05;
    public const byte AsciiString = 0x06;
}

/// <summary>
/// A single GDSII stream record: the record type byte, the data type byte and the
/// payload with the 4 header bytes (length, record type, data type) stripped.
/// </summary>
internal sealed record GdsRecord(byte RecordType, byte DataType, byte[] Payload);

/// <summary>
/// Low-level reader for the GDSII stream format: record framing (2-byte big-endian
/// length, record type and data type bytes) and payload decoding (big-endian
/// integers, GDS Real4/Real8 floating point, ASCII strings, XY point lists).
/// All members are stateless; the element-assembly state machine that consumes
/// the decoded records lives in <see cref="GdsReader"/>.
/// </summary>
internal static class GdsRecordReader
{
    /// <summary>
    /// Reads the next record from <paramref name="stream"/>: a 2-byte big-endian
    /// length (including the 4 header bytes), a 1-byte record type, a 1-byte data
    /// type and the payload. Returns null only when the stream ends cleanly at a
    /// record boundary (zero bytes available at all).
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown on truncated or malformed records.</exception>
    public static async Task<GdsRecord?> ReadNextAsync(Stream stream, CancellationToken ct)
    {
        var header = await TryReadExactAsync(stream, 4, ct).ConfigureAwait(false);
        if (header is null)
            return null;

        int recordLength = (header[0] << 8) | header[1];
        if (recordLength < 4)
            throw new InvalidDataException($"Invalid GDS record length {recordLength} — must be at least 4 bytes.");

        var payload = await ReadExactAsync(stream, recordLength - 4, ct).ConfigureAwait(false);
        return new GdsRecord(header[2], header[3], payload);
    }

    // ── Payload decoding ─────────────────────────────────────────────────────

    public static List<GdsPoint> ReadPoints(byte[] payload)
    {
        if (payload.Length % 8 != 0)
            throw new InvalidDataException($"Malformed GDS XY record — {payload.Length} payload bytes is not a multiple of 8.");

        var points = new List<GdsPoint>(payload.Length / 8);
        for (int i = 0; i < payload.Length; i += 8)
        {
            // Kept in database units here; scaled to micrometers at ENDEL time.
            points.Add(new GdsPoint(ReadInt4(payload, i), ReadInt4(payload, i + 4)));
        }
        return points;
    }

    public static string ReadString(byte[] payload) =>
        Encoding.ASCII.GetString(payload).TrimEnd('\0');

    public static int ReadInt2(byte[] payload, int offset)
    {
        if (payload.Length < offset + 2)
            throw new InvalidDataException("Malformed GDS record — truncated 2-byte integer.");
        return BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(offset, 2));
    }

    public static int ReadInt4(byte[] payload, int offset)
    {
        if (payload.Length < offset + 4)
            throw new InvalidDataException("Malformed GDS record — truncated 4-byte integer.");
        return BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
    }

    public static double ReadReal(byte dataType, byte[] payload) => dataType switch
    {
        GdsDataTypes.Real8 when payload.Length >= 8 => ReadReal8(payload, 0),
        GdsDataTypes.Real4 when payload.Length >= 4 => ReadReal4(payload, 0),
        _ => throw new InvalidDataException($"Malformed GDS real-valued record — unexpected data type 0x{dataType:X2}."),
    };

    /// <summary>
    /// Decodes a GDS 8-byte real: sign bit, 7-bit excess-64 exponent and a
    /// 56-bit base-16 mantissa: value = mantissa × 16^(exponent − 64) / 2^56.
    /// </summary>
    public static double ReadReal8(byte[] payload, int offset)
    {
        if (payload.Length < offset + 8)
            throw new InvalidDataException("Malformed GDS record — truncated 8-byte real.");

        bool negative = (payload[offset] & 0x80) != 0;
        int exponent = (payload[offset] & 0x7F) - 64;

        ulong mantissa = 0;
        for (int i = 1; i < 8; i++)
            mantissa = (mantissa << 8) | payload[offset + i];

        if (mantissa == 0)
            return 0.0;

        double value = Math.ScaleB(mantissa, 4 * exponent - 56);
        return negative ? -value : value;
    }

    /// <summary>Decodes a GDS 4-byte real (same format as the 8-byte real, 24-bit mantissa).</summary>
    private static double ReadReal4(byte[] payload, int offset)
    {
        if (payload.Length < offset + 4)
            throw new InvalidDataException("Malformed GDS record — truncated 4-byte real.");

        bool negative = (payload[offset] & 0x80) != 0;
        int exponent = (payload[offset] & 0x7F) - 64;
        uint mantissa = (uint)((payload[offset + 1] << 16) | (payload[offset + 2] << 8) | payload[offset + 3]);

        if (mantissa == 0)
            return 0.0;

        double value = Math.ScaleB(mantissa, 4 * exponent - 24);
        return negative ? -value : value;
    }

    // ── Stream reading ───────────────────────────────────────────────────────

    /// <summary>Reads exactly <paramref name="count"/> bytes or throws on end of stream.</summary>
    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = await TryReadExactAsync(stream, count, ct).ConfigureAwait(false);
        return buffer ?? throw new InvalidDataException("Unexpected end of GDS stream — truncated record.");
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes, returning null only when the
    /// stream ends cleanly at a record boundary (zero bytes available at all).
    /// </summary>
    private static async Task<byte[]?> TryReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                    return null; // clean EOF before the first byte
                throw new InvalidDataException("Unexpected end of GDS stream — truncated record.");
            }
            offset += read;
        }
        return buffer;
    }
}
