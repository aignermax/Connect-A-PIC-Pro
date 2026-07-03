namespace CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;

/// <summary>
/// Instructs the gdsfactory host emitter to compose a mixed-backend GDS (issue #646):
/// the listed instances were already rendered by the Nazca emitter into
/// <see cref="NazcaGdsFileName"/> (a GDS next to the host script), so the host skips
/// their stub/ubcpdk placement and instead imports that GDS via <c>gf.import_gds</c>
/// at the origin — both backends place at the same absolute mapper coordinates, so
/// no additional transform is needed.
/// </summary>
/// <param name="MergedIdentifiers">Identifiers of instances rendered in the Nazca part.</param>
/// <param name="NazcaGdsFileName">File name (no directory) of the Nazca-part GDS,
/// resolved relative to the host script at run time.</param>
public record NazcaGdsMerge(
    IReadOnlySet<string> MergedIdentifiers,
    string NazcaGdsFileName);
