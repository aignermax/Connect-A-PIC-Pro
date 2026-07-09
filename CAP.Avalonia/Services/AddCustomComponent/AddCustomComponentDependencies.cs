using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Bundles the "add custom component → user PDK" feature's optional collaborators (issue
/// #656) so <c>LeftPanelViewModel</c> takes one constructor parameter instead of three.
/// </summary>
public sealed record AddCustomComponentDependencies(
    ComponentGeometryExtractor Extractor, IFdtdSMatrixService? Fdtd, UserPdkStore UserPdkStore);
