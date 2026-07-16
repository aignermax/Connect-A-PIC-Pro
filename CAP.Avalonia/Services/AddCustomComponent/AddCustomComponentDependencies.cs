using CAP_Core;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Bundles the "add custom component → user PDK" feature's optional collaborators (issue
/// #656) so <c>LeftPanelViewModel</c> takes one constructor parameter instead of three.
/// <paramref name="ErrorConsole"/> receives the raw Python traceback when the editor's
/// preview fails with a recognised foundry-package error and the status bar shows the
/// friendly hint instead.
/// </summary>
public sealed record AddCustomComponentDependencies(
    ComponentGeometryExtractor Extractor, IFdtdSMatrixService? Fdtd, UserPdkStore UserPdkStore,
    ErrorConsoleService? ErrorConsole = null);
