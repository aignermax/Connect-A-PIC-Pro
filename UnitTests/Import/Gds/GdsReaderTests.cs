using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Unit tests for <see cref="GdsReader"/>. All fixtures are built in memory via
/// <see cref="GdsTestWriter"/> — no external files. Unless stated otherwise the
/// test libraries use the standard UNITS pair (1e-3, 1e-9), i.e. 1 database
/// unit = 1 nm, so 1000 database units = 1 µm.
/// </summary>
public class GdsReaderTests
{
    private static async Task<GdsLibrary> ReadLibrary(byte[] gdsBytes)
    {
        using var stream = new MemoryStream(gdsBytes);
        return await new GdsReader().ReadAsync(stream);
    }

    // ── Library structure ────────────────────────────────────────────────────

    [Fact]
    public async Task ParsesLibraryNameAndUnits()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue("mylib")
            .BeginCell("TOP").EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        library.Name.ShouldBe("mylib");
        // The GDS 8-byte real round-trip must be exact — no tolerance.
        library.UserUnitsPerDatabaseUnit.ShouldBe(1e-3);
        library.DatabaseUnitInMeters.ShouldBe(1e-9);
        library.DatabaseUnitsToMicrometers.ShouldBe(1e-3);
        library.Cells.Keys.ShouldBe(new[] { "TOP" });
    }

    [Fact]
    public async Task Boundary_BecomesPolygonWithMicrometerCoordinates()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(1, 2, (0, 0), (1000, 0), (1000, 2000), (0, 2000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        var polygon = library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsPolygon>();
        polygon.Layer.ShouldBe(1);
        polygon.DataType.ShouldBe(2);
        // 1 db unit = 1 nm → 1000 db units = 1 µm; GDS repeats the first point last.
        polygon.Points.ShouldBe(new[]
        {
            new GdsPoint(0, 0), new GdsPoint(1, 0), new GdsPoint(1, 2), new GdsPoint(0, 2), new GdsPoint(0, 0),
        });
    }

    [Fact]
    public async Task Boundary_NegativeCoordinates_ParseSigned()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(0, 0, (-1000, -2000), (1000, -2000), (0, 3000), (-1000, -2000))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        var polygon = library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsPolygon>();
        polygon.Points[0].ShouldBe(new GdsPoint(-1, -2));
        polygon.Points[2].ShouldBe(new GdsPoint(0, 3));
    }

    [Fact]
    public async Task Text_ParsesTrimmedStringPositionAndTextType()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                // "PIN" is 3 chars → NUL-padded to 4 on the wire; reader must trim.
                .Text(7, 5, "PIN", 1500, -500)
            .EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        var text = library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsText>();
        text.Layer.ShouldBe(7);
        text.TextType.ShouldBe(5);
        text.Text.ShouldBe("PIN");
        text.Position.ShouldBe(new GdsPoint(1.5, -0.5));
    }

    [Fact]
    public async Task Path_CapturesWidthAndPathType()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Path(3, 1, widthDbUnits: 500, pathType: 2, (0, 0), (4000, 0), (4000, 2000))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        var path = library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsPath>();
        path.Layer.ShouldBe(3);
        path.DataType.ShouldBe(1);
        path.WidthMicrometers.ShouldBe(0.5);
        path.PathType.ShouldBe(2);
        path.Points.ShouldBe(new[]
        {
            new GdsPoint(0, 0), new GdsPoint(4, 0), new GdsPoint(4, 2),
        });
    }

    // ── References ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SRef_ParsesTransformFieldsExactly()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 10000, 20000, angleDegrees: -0.5, magnification: 2.0)
            .EndCell()
            .BeginCell("CHILD").EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        var reference = library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsReference>();
        reference.CellName.ShouldBe("CHILD");
        reference.Offset.ShouldBe(new GdsPoint(10, 20));
        // Exact 8-byte real round-trip through the file — no tolerance.
        reference.AngleDegrees.ShouldBe(-0.5);
        reference.Magnification.ShouldBe(2.0);
        reference.Reflected.ShouldBeFalse();
        reference.IsArray.ShouldBeFalse();
    }

    [Fact]
    public async Task SRef_MagnificationAsReal4_Parses()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 0, 0, magnification: 2.0, magnificationAsReal4: true)
            .EndCell()
            .BeginCell("CHILD").EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        var reference = library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsReference>();
        reference.Magnification.ShouldBe(2.0);
    }

    [Fact]
    public async Task STrans_ReflectionFlag_IsParsed()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 0, 0, reflected: true)
            .EndCell()
            .BeginCell("CHILD").EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        var reference = library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsReference>();
        reference.Reflected.ShouldBeTrue();
        reference.TransformFlags.ShouldBe(0x8000);
    }

    [Fact]
    public async Task ARef_ParsesColRowAndSpacings()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .ARef("CHILD", columns: 3, rows: 2, originX: 1000, originY: 2000,
                    columnSpacingDbUnits: 4000, rowSpacingDbUnits: 3000)
            .EndCell()
            .BeginCell("CHILD").EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        var reference = library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsReference>();
        reference.IsArray.ShouldBeTrue();
        reference.Columns.ShouldBe(3);
        reference.Rows.ShouldBe(2);
        reference.Offset.ShouldBe(new GdsPoint(1, 2));
        reference.ColumnSpacingMicrometers.ShouldBe(4.0);
        reference.RowSpacingMicrometers.ShouldBe(3.0);
    }

    [Fact]
    public async Task ReferenceToCellDefinedLater_Parses()
    {
        // Reading must be order-tolerant: TOP references CHILD before CHILD is defined.
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 5000, 0)
            .EndCell()
            .BeginCell("CHILD")
                .Boundary(1, 0, (0, 0), (1000, 0), (1000, 1000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        library.Cells.Keys.ShouldBe(new[] { "TOP", "CHILD" });
        var flattened = new GdsCellFlattener(library).Flatten("TOP");
        flattened.Polygons.ShouldHaveSingleItem().Points[0].ShouldBe(new GdsPoint(5, 0));
    }

    // ── Top cells ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TopCellCandidates_ReportsUnreferencedCells()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 0, 0)
            .EndCell()
            .BeginCell("CHILD").EndCell()
            .BeginCell("OTHER_TOP").EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        library.TopCellCandidates.ShouldBe(new[] { "TOP", "OTHER_TOP" });
    }

    // ── Robustness ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownRecords_AreSkipped()
    {
        // 0x7F is not a defined GDS record type — a forward-compatibility probe.
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .WriteRecord(0x7F, 0x03, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })
            .BeginCell("TOP")
                .Boundary(1, 0, (0, 0), (1000, 0), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsPolygon>();
    }

    [Fact]
    public async Task TruncatedFile_Throws()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(1, 0, (0, 0), (1000, 0), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        // Cut mid-record: ENDLIB is a bare 4-byte header record (no payload),
        // so dropping the last 2 bytes truncates the record HEADER itself.
        var truncated = gds[..^2];

        await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(truncated));
    }

    [Fact]
    public async Task EmptyFile_Throws()
    {
        await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(Array.Empty<byte>()));
    }

    [Fact]
    public async Task MissingUnits_Throws()
    {
        var gds = GdsTestWriter.Create()
            .Header().BeginLibrary().LibraryName("nounits") // no UNITS record
            .BeginCell("TOP")
                .Boundary(1, 0, (0, 0), (1000, 0), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var ex = await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(gds));
        ex.Message.ShouldContain("UNITS");
    }

    // ── Per-read state / structural robustness ───────────────────────────────

    [Fact]
    public async Task ReadAsync_TwiceOnOneInstance_ReturnsFreshLibraryEachTime()
    {
        var reader = new GdsReader();

        var first = await reader.ReadAsync(new MemoryStream(GdsTestWriter.Create()
            .StandardPrologue("lib1")
            .BeginCell("A").EndCell()
            .EndLibrary()
            .ToArray()));
        var second = await reader.ReadAsync(new MemoryStream(GdsTestWriter.Create()
            .StandardPrologue("lib2")
            .BeginCell("B").EndCell()
            .EndLibrary()
            .ToArray()));

        first.Name.ShouldBe("lib1");
        second.Name.ShouldBe("lib2");
        // The second read must not return the first stream's stale library.
        second.Cells.Keys.ShouldBe(new[] { "B" });
    }

    [Fact]
    public async Task DuplicateCellName_ThrowsNamingTheCell()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP").EndCell()
            .BeginCell("TOP").EndCell()
            .EndLibrary()
            .ToArray();

        var ex = await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(gds));
        ex.Message.ShouldContain("Duplicate");
        ex.Message.ShouldContain("TOP");
    }

    [Fact]
    public async Task EndStrWithOpenElement_ThrowsInsteadOfMisleadingEndelError()
    {
        // BOUNDARY without its closing ENDEL, immediately followed by ENDSTR.
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .WriteRecord(GdsRecordTypes.Boundary, GdsDataTypes.NoData, Array.Empty<byte>())
                .WriteRecord(GdsRecordTypes.Layer, GdsDataTypes.Int2, GdsTestWriter.Int2Bytes(1))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var ex = await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(gds));
        ex.Message.ShouldContain("ENDSTR");
        ex.Message.ShouldContain("ENDEL");
    }

    [Theory]
    [InlineData(0, 2)]   // zero columns
    [InlineData(2, 0)]   // zero rows
    [InlineData(-1, 2)]  // negative columns
    public async Task ARef_ColRowBelowOne_ThrowsAtReadTime(int columns, int rows)
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .ARef("CHILD", columns, rows, originX: 0, originY: 0,
                    columnSpacingDbUnits: 4000, rowSpacingDbUnits: 3000)
            .EndCell()
            .BeginCell("CHILD").EndCell()
            .EndLibrary()
            .ToArray();

        var ex = await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(gds));
        ex.Message.ShouldContain("COLROW");
    }

    [Fact]
    public async Task ARef_MemberCountAboveSanityCap_ThrowsAtReadTime()
    {
        // 400 × 300 = 120,000 members > 100,000 cap — expansion would hang/OOM the import.
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .ARef("CHILD", columns: 400, rows: 300, originX: 0, originY: 0,
                    columnSpacingDbUnits: 4000, rowSpacingDbUnits: 3000)
            .EndCell()
            .BeginCell("CHILD").EndCell()
            .EndLibrary()
            .ToArray();

        var ex = await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(gds));
        ex.Message.ShouldContain("sanity cap");
    }

    [Fact]
    public async Task Boundary_UnclosedRing_IsClosedOnRead()
    {
        // Writers SHOULD repeat the first point at the end; this triangle does
        // not — the reader must auto-close it (KLayout behaves the same).
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(1, 0, (0, 0), (1000, 0), (0, 1000))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var library = await ReadLibrary(gds);

        var polygon = library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsPolygon>();
        polygon.Points.Count.ShouldBe(4);
        polygon.Points[^1].ShouldBe(polygon.Points[0], "the stored ring must be closed");
    }

    [Fact]
    public async Task MalformedPayload_ErrorNamesTheRecordTypeAndKeepsInnerException()
    {
        // UNITS with a 4-byte payload: the decode error must carry record context.
        var gds = GdsTestWriter.Create()
            .Header().BeginLibrary().LibraryName("badunits")
            .WriteRecord(GdsRecordTypes.Units, GdsDataTypes.Real8, new byte[4])
            .BeginCell("TOP").EndCell()
            .EndLibrary()
            .ToArray();

        var ex = await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(gds));
        ex.Message.ShouldContain("UNITS");
        ex.InnerException.ShouldBeOfType<InvalidDataException>();
    }

    // ── Record framing ───────────────────────────────────────────────────────

    [Fact]
    public async Task RecordLengthBelowFour_Throws()
    {
        // A record length of 3 is impossible — the header alone is 4 bytes.
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .WriteRawBytes(0, 3, 0x08, 0x00)
            .BeginCell("TOP").EndCell()
            .EndLibrary()
            .ToArray();

        await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(gds));
    }

    [Fact]
    public async Task XyPayloadNotMultipleOfEight_Throws()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .WriteRecord(GdsRecordTypes.Boundary, GdsDataTypes.NoData, Array.Empty<byte>())
                .WriteRecord(GdsRecordTypes.Layer, GdsDataTypes.Int2, GdsTestWriter.Int2Bytes(1))
                // 12 payload bytes is not a whole number of (int32 X, int32 Y) pairs.
                .WriteRecord(GdsRecordTypes.XY, GdsDataTypes.Int4, new byte[12])
            .EndCell()
            .EndLibrary()
            .ToArray();

        var ex = await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(gds));
        ex.Message.ShouldContain("XY");
    }

    [Fact]
    public async Task TruncatedPayloadRegion_Throws()
    {
        // The XY record header announces 20 payload bytes but the stream ends
        // right after the header — a payload-region truncation.
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .WriteRawBytes(0, 24, 0x10, 0x03)
            .ToArray();

        await Should.ThrowAsync<InvalidDataException>(() => ReadLibrary(gds));
    }

    [Fact]
    public async Task ChunkedStream_ParsesFullLibrary()
    {
        // A stream that serves at most 3 bytes per read exercises the
        // partial-read loop in GdsRecordReader for header AND payload alike.
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(1, 0, (0, 0), (1000, 0), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        using var stream = new ChunkedStream(new MemoryStream(gds), maxChunk: 3);
        var library = await new GdsReader().ReadAsync(stream);

        library.Cells.Keys.ShouldBe(new[] { "TOP" });
        library.Cells["TOP"].Elements.ShouldHaveSingleItem().ShouldBeOfType<GdsPolygon>();
    }

    /// <summary>Stream wrapper that returns at most <paramref name="maxChunk"/> bytes per read.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxChunk;

        public ChunkedStream(Stream inner, int maxChunk)
        {
            _inner = inner;
            _maxChunk = maxChunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, _maxChunk));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _maxChunk)], cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ── GDS 8-byte real codec ────────────────────────────────────────────────

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1e-9)]
    [InlineData(1.0)]
    [InlineData(90.0)]
    [InlineData(-0.5)]
    public void Real8_RoundTripsBitExactly(double value)
    {
        var encoded = GdsTestWriter.EncodeReal8(value);

        encoded.Length.ShouldBe(8);
        // Exact equality is the point of the test: the 56-bit base-16 mantissa
        // holds every binary64 significand without loss.
        GdsRecordReader.ReadReal8(encoded, 0).ShouldBe(value);
    }

    [Fact]
    public void Real8_EncodesKnownLiterals()
    {
        // Reference encoding from the GDSII spec: 1.0 → 0x41 0x10 followed by zeros.
        GdsTestWriter.EncodeReal8(1.0).ShouldBe(new byte[] { 0x41, 0x10, 0, 0, 0, 0, 0, 0 });
        GdsTestWriter.EncodeReal8(0.0).ShouldBe(new byte[8]);
    }

    [Theory]
    [InlineData(1e300)]   // exponent would exceed 127
    [InlineData(-1e300)]
    [InlineData(1e-300)]  // exponent would underflow below 0
    public void Real8_UnrepresentableValue_Throws(double value) =>
        Should.Throw<ArgumentOutOfRangeException>(() => GdsTestWriter.EncodeReal8(value));

    [Fact]
    public void Real4_UnrepresentableValue_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => GdsTestWriter.EncodeReal4(1e300));
}
