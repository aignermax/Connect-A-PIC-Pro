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

    private readonly GdsLibrary _library = new();
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
    /// </summary>
    /// <param name="stream">Readable, seekable-or-sequential stream positioned at the first record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed <see cref="GdsLibrary"/> with all coordinates converted to micrometers.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown on truncated records, missing UNITS, or other structural format errors.
    /// </exception>
    public async Task<GdsLibrary> ReadAsync(Stream stream, CancellationToken ct = default)
    {
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

    // ── Record dispatch ──────────────────────────────────────────────────────

    private void HandleRecord(byte recordType, byte dataType, byte[] payload)
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
                _library.Cells[_currentCell.Name] = _currentCell;
                break;

            case GdsRecordTypes.EndStr:
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
                    Points = RequirePoints().Select(p => Scale(p, toMicrometers)).ToList(),
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
