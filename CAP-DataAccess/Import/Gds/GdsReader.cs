namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Streaming reader for the GDSII stream format (Calma GDSII).
///
/// Each record consists of a 2-byte big-endian length (including the 4 header
/// bytes), a 1-byte record type, a 1-byte data type and the payload. Records are
/// read one at a time so arbitrarily large files never require a full in-memory
/// copy. Unknown record types are skipped via their length field, making the
/// reader forward-compatible with records it does not understand.
///
/// Reading is order-tolerant with respect to cell definitions: a cell may
/// reference (SREF/AREF) cells that are only defined later in the stream.
/// References are resolved by <see cref="GdsCellFlattener"/>, not here.
///
/// Throws <see cref="InvalidDataException"/> on truncated records, a missing
/// UNITS record, or other structural format errors.
/// </summary>
public sealed class GdsReader
{
    /// <summary>STRANS bit 15: reflect about the X axis before rotating.</summary>
    public const int STransReflectionFlag = 0x8000;

    // Per-read state — reset at the top of every ReadAsync call (see ResetState).
    private GdsLibrary _library = new();
    private bool _hasUnits;
    private bool _sawEndLib;

    private GdsCell? _currentCell;
    private ElementKind _elementKind = ElementKind.None;
    private int _layer;
    private int _dataType;
    private int _textType;
    private int _pathType;
    private int _widthDatabaseUnits;
    private int _columns = 1;
    private int _rows = 1;
    private int _transFlags;
    private double _magnification = 1.0;
    private double _angleDegrees;
    private string _text = string.Empty;
    private string _referencedCellName = string.Empty;
    private List<GdsPoint>? _points;

    private enum ElementKind { None, Boundary, Path, Box, Text, SRef, ARef }

    /// <summary>
    /// Reads a GDSII stream from <paramref name="stream"/> and returns the parsed library.
    /// The stream is consumed record by record; it is not disposed by this method.
    /// One reader instance may parse several streams in sequence — all per-read
    /// state is reset at the start of each call.
    /// </summary>
    /// <param name="stream">Readable, seekable-or-sequential stream positioned at the first record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed <see cref="GdsLibrary"/> with all coordinates converted to micrometers.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown on truncated records, missing UNITS, or other structural format errors.
    /// </exception>
    public async Task<GdsLibrary> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        ResetState();

        while (true)
        {
            var record = await GdsRecordReader.ReadNextAsync(stream, ct).ConfigureAwait(false);
            if (record is null)
                break; // clean end of stream at a record boundary

            HandleRecord(record.RecordType, record.DataType, record.Payload);

            if (_sawEndLib)
                break;
        }

        if (!_sawEndLib)
            throw new InvalidDataException("Unexpected end of GDS stream — the file is truncated (no ENDLIB record).");
        if (!_hasUnits)
            throw new InvalidDataException("GDS file has no UNITS record — cannot determine the database-unit size.");

        return _library;
    }

    /// <summary>
    /// Resets every piece of per-read state so a second <see cref="ReadAsync"/>
    /// call on the same instance starts clean — without this it would silently
    /// return the FIRST stream's library.
    /// </summary>
    private void ResetState()
    {
        _library = new GdsLibrary();
        _hasUnits = false;
        _sawEndLib = false;
        _currentCell = null;
        _elementKind = ElementKind.None;
        _layer = 0;
        _dataType = 0;
        _textType = 0;
        _pathType = 0;
        _widthDatabaseUnits = 0;
        _columns = 1;
        _rows = 1;
        _transFlags = 0;
        _magnification = 1.0;
        _angleDegrees = 0.0;
        _text = string.Empty;
        _referencedCellName = string.Empty;
        _points = null;
    }

    // ── Record dispatch ──────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches one record, wrapping any decode/structural error with the
    /// record type name so a malformed file points at the offending record
    /// (the original exception is kept as the inner exception).
    /// </summary>
    private void HandleRecord(byte recordType, byte dataType, byte[] payload)
    {
        try
        {
            DispatchRecord(recordType, dataType, payload);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"Invalid GDS {RecordTypeName(recordType)} record: {ex.Message}", ex);
        }
    }

    private void DispatchRecord(byte recordType, byte dataType, byte[] payload)
    {
        switch (recordType)
        {
            case GdsRecordTypes.Header:
            case GdsRecordTypes.BgnLib:
                // Version number / library timestamps — not needed for geometry.
                break;

            case GdsRecordTypes.BgnStr:
                // Structure timestamps — not needed; opens a new cell.
                _currentCell = new GdsCell();
                break;

            case GdsRecordTypes.LibName:
                _library.Name = GdsRecordReader.ReadString(payload);
                break;

            case GdsRecordTypes.Units:
                if (dataType != GdsDataTypes.Real8 || payload.Length < 16)
                    throw new InvalidDataException("Malformed GDS UNITS record — expected two 8-byte reals.");
                _library.UserUnitsPerDatabaseUnit = GdsRecordReader.ReadReal8(payload, 0);
                _library.DatabaseUnitInMeters = GdsRecordReader.ReadReal8(payload, 8);
                _hasUnits = true;
                break;

            case GdsRecordTypes.StrName:
                if (_currentCell is null)
                    throw new InvalidDataException("STRNAME record outside of a cell definition.");
                _currentCell.Name = GdsRecordReader.ReadString(payload);
                if (!_library.Cells.TryAdd(_currentCell.Name, _currentCell))
                {
                    throw new InvalidDataException(
                        $"Duplicate GDS cell name '{_currentCell.Name}' — cell names must be unique within a library.");
                }
                break;

            case GdsRecordTypes.EndStr:
                if (_elementKind != ElementKind.None)
                {
                    throw new InvalidDataException(
                        $"ENDSTR reached while a {_elementKind} element is still open — " +
                        "the element is missing its ENDEL record.");
                }
                _currentCell = null;
                break;

            case GdsRecordTypes.EndLib:
                _sawEndLib = true;
                break;

            case GdsRecordTypes.Boundary: BeginElement(ElementKind.Boundary); break;
            case GdsRecordTypes.Path: BeginElement(ElementKind.Path); break;
            case GdsRecordTypes.Box: BeginElement(ElementKind.Box); break;
            case GdsRecordTypes.Text: BeginElement(ElementKind.Text); break;
            case GdsRecordTypes.SRef: BeginElement(ElementKind.SRef); break;
            case GdsRecordTypes.ARef: BeginElement(ElementKind.ARef); break;

            case GdsRecordTypes.Layer:
                _layer = GdsRecordReader.ReadInt2(payload, 0);
                break;

            case GdsRecordTypes.DataType:
            case GdsRecordTypes.BoxType:
                _dataType = GdsRecordReader.ReadInt2(payload, 0);
                break;

            case GdsRecordTypes.TextType:
                _textType = GdsRecordReader.ReadInt2(payload, 0);
                break;

            case GdsRecordTypes.PathType:
                _pathType = GdsRecordReader.ReadInt2(payload, 0);
                break;

            case GdsRecordTypes.Width:
                _widthDatabaseUnits = GdsRecordReader.ReadInt4(payload, 0);
                break;

            case GdsRecordTypes.XY:
                _points = GdsRecordReader.ReadPoints(payload);
                break;

            case GdsRecordTypes.String:
                _text = GdsRecordReader.ReadString(payload);
                break;

            case GdsRecordTypes.SName:
                _referencedCellName = GdsRecordReader.ReadString(payload);
                break;

            case GdsRecordTypes.ColRow:
                if (payload.Length < 4)
                    throw new InvalidDataException("Malformed GDS COLROW record — expected two 2-byte integers.");
                _columns = GdsRecordReader.ReadInt2(payload, 0);
                _rows = GdsRecordReader.ReadInt2(payload, 2);
                if (_columns < 1 || _rows < 1)
                {
                    throw new InvalidDataException(
                        $"Malformed GDS COLROW record — columns and rows must be ≥ 1, got {_columns}×{_rows}.");
                }
                if ((long)_columns * _rows > 100_000)
                {
                    // Sanity cap against hostile/insane AREFs: expansion happens
                    // eagerly in the flattener, so an absurd array would hang or
                    // OOM the import instead of failing fast here.
                    throw new InvalidDataException(
                        $"GDS AREF declares {_columns}×{_rows} = {(long)_columns * _rows} array members — " +
                        "above the 100,000-member sanity cap.");
                }
                break;

            case GdsRecordTypes.STrans:
                _transFlags = GdsRecordReader.ReadInt2(payload, 0) & 0xFFFF;
                break;

            case GdsRecordTypes.Mag:
                _magnification = GdsRecordReader.ReadReal(dataType, payload);
                break;

            case GdsRecordTypes.Angle:
                _angleDegrees = GdsRecordReader.ReadReal(dataType, payload);
                break;

            case GdsRecordTypes.EndEl:
                EndElement();
                break;

            // Unknown or unsupported record types (PRESENTATION, NODE, PROPATTR, …):
            // the payload has already been consumed via the record length, so the
            // record is skipped implicitly — forward-compatible by construction.
        }
    }

    // ── Element assembly ─────────────────────────────────────────────────────

    private void BeginElement(ElementKind kind)
    {
        if (_currentCell is null)
            throw new InvalidDataException("GDS element record outside of a cell definition.");
        if (_elementKind != ElementKind.None)
            throw new InvalidDataException("GDS element record started before the previous element was closed (ENDEL).");

        _elementKind = kind;
        _layer = 0;
        _dataType = 0;
        _textType = 0;
        _pathType = 0;
        _widthDatabaseUnits = 0;
        _columns = 1;
        _rows = 1;
        _transFlags = 0;
        _magnification = 1.0;
        _angleDegrees = 0.0;
        _text = string.Empty;
        _referencedCellName = string.Empty;
        _points = null;
    }

    private void EndElement()
    {
        if (_elementKind == ElementKind.None || _currentCell is null)
            throw new InvalidDataException("ENDEL record without a matching element start record.");

        double toMicrometers = RequireUnits();

        switch (_elementKind)
        {
            case ElementKind.Boundary:
            case ElementKind.Box:
                _currentCell.Elements.Add(new GdsPolygon
                {
                    Layer = _layer,
                    DataType = _dataType,
                    Points = CloseRing(RequirePoints().Select(p => Scale(p, toMicrometers)).ToList()),
                });
                break;

            case ElementKind.Path:
                _currentCell.Elements.Add(new GdsPath
                {
                    Layer = _layer,
                    DataType = _dataType,
                    PathType = _pathType,
                    WidthMicrometers = Math.Abs(_widthDatabaseUnits) * toMicrometers,
                    Points = RequirePoints().Select(p => Scale(p, toMicrometers)).ToList(),
                });
                break;

            case ElementKind.Text:
                _currentCell.Elements.Add(new GdsText
                {
                    Layer = _layer,
                    TextType = _textType,
                    Text = _text,
                    Position = Scale(SinglePoint(), toMicrometers),
                    AngleDegrees = _angleDegrees,
                });
                break;

            case ElementKind.SRef:
            case ElementKind.ARef:
                _currentCell.Elements.Add(BuildReference(toMicrometers));
                break;
        }

        _elementKind = ElementKind.None;
    }

    private GdsReference BuildReference(double toMicrometers)
    {
        if (string.IsNullOrEmpty(_referencedCellName))
            throw new InvalidDataException($"GDS {(_elementKind == ElementKind.ARef ? "AREF" : "SREF")} without an SNAME record.");

        var points = RequirePoints();

        if (_elementKind == ElementKind.SRef)
        {
            return new GdsReference
            {
                CellName = _referencedCellName,
                Offset = Scale(points[0], toMicrometers),
                AngleDegrees = _angleDegrees,
                Magnification = _magnification,
                Reflected = (_transFlags & STransReflectionFlag) != 0,
                TransformFlags = _transFlags,
            };
        }

        // AREF: three XY points — origin, end of the column lattice vector,
        // end of the row lattice vector. The lattice vectors already include the
        // reference rotation/reflection, so only their lengths (as spacings) are
        // kept here; the flattener re-applies the rotation when expanding.
        if (points.Count < 3)
            throw new InvalidDataException($"GDS AREF to '{_referencedCellName}' has {points.Count} XY points — expected 3.");

        double columnSpacing = Distance(points[0], points[1]) / Math.Max(1, _columns) * toMicrometers;
        double rowSpacing = Distance(points[0], points[2]) / Math.Max(1, _rows) * toMicrometers;

        return new GdsReference
        {
            CellName = _referencedCellName,
            Offset = Scale(points[0], toMicrometers),
            AngleDegrees = _angleDegrees,
            Magnification = _magnification,
            Reflected = (_transFlags & STransReflectionFlag) != 0,
            TransformFlags = _transFlags,
            Columns = _columns,
            Rows = _rows,
            ColumnSpacingMicrometers = columnSpacing,
            RowSpacingMicrometers = rowSpacing,
        };
    }

    // ── Small helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Normalizes a BOUNDARY/BOX point list to a closed ring: the spec asks
    /// writers to repeat the first point at the end, but not all do — mainstream
    /// readers (KLayout included) auto-close, and our consumers (the outline
    /// simplifier) rely on the closed-ring contract, so an unclosed polygon gets
    /// its first point appended here.
    /// </summary>
    private static List<GdsPoint> CloseRing(List<GdsPoint> points)
    {
        if (points.Count > 1 && !points[0].Equals(points[^1]))
            points.Add(points[0]);
        return points;
    }

    /// <summary>Display name of a record type for error messages (hex fallback for unknown types).</summary>
    private static string RecordTypeName(byte recordType) => recordType switch
    {
        GdsRecordTypes.Header => "HEADER",
        GdsRecordTypes.BgnLib => "BGNLIB",
        GdsRecordTypes.LibName => "LIBNAME",
        GdsRecordTypes.Units => "UNITS",
        GdsRecordTypes.EndLib => "ENDLIB",
        GdsRecordTypes.BgnStr => "BGNSTR",
        GdsRecordTypes.StrName => "STRNAME",
        GdsRecordTypes.EndStr => "ENDSTR",
        GdsRecordTypes.Boundary => "BOUNDARY",
        GdsRecordTypes.Path => "PATH",
        GdsRecordTypes.SRef => "SREF",
        GdsRecordTypes.ARef => "AREF",
        GdsRecordTypes.Text => "TEXT",
        GdsRecordTypes.Layer => "LAYER",
        GdsRecordTypes.DataType => "DATATYPE",
        GdsRecordTypes.Width => "WIDTH",
        GdsRecordTypes.XY => "XY",
        GdsRecordTypes.EndEl => "ENDEL",
        GdsRecordTypes.SName => "SNAME",
        GdsRecordTypes.ColRow => "COLROW",
        GdsRecordTypes.TextType => "TEXTTYPE",
        GdsRecordTypes.String => "STRING",
        GdsRecordTypes.STrans => "STRANS",
        GdsRecordTypes.Mag => "MAG",
        GdsRecordTypes.Angle => "ANGLE",
        GdsRecordTypes.PathType => "PATHTYPE",
        GdsRecordTypes.Box => "BOX",
        GdsRecordTypes.BoxType => "BOXTYPE",
        _ => $"0x{recordType:X2}",
    };

    private double RequireUnits() =>
        _hasUnits
            ? _library.DatabaseUnitsToMicrometers
            : throw new InvalidDataException("GDS file has no UNITS record — cannot convert coordinates to micrometers.");

    private List<GdsPoint> RequirePoints() =>
        _points is { Count: > 0 } points
            ? points
            : throw new InvalidDataException("GDS element without an XY record.");

    private GdsPoint SinglePoint()
    {
        var points = RequirePoints();
        if (points.Count != 1)
            throw new InvalidDataException($"GDS TEXT element has {points.Count} XY points — expected exactly 1.");
        return points[0];
    }

    private static GdsPoint Scale(GdsPoint databaseUnits, double factor) =>
        new(databaseUnits.X * factor, databaseUnits.Y * factor);

    private static double Distance(GdsPoint a, GdsPoint b) =>
        Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
}
