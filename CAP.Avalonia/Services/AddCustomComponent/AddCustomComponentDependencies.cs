using CAP_Core;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Bundles the "add custom component → user PDK" feature's optional collaborators so
/// <c>LeftPanelViewModel</c> takes one constructor parameter instead of several.
/// </summary>
public sealed record AddCustomComponentDependencies(
    ComponentGeometryExtractor Extractor, IFdtdSMatrixService? Fdtd, UserPdkStore UserPdkStore,
    ErrorConsoleService? ErrorConsole = null);
