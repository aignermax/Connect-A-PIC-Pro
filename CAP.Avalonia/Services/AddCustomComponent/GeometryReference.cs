namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>Which geometry engine renders/exports a custom component.</summary>
public enum GeometryBackend
{
    /// <summary>Nazca PDK cell, rendered in module/function mode.</summary>
    Nazca,

    /// <summary>gdsfactory PCell, rendered via a raw-code import wrapper.</summary>
    GdsFactory,
}

/// <summary>
/// A reference to a component-producing function in a Python module, e.g.
/// module "cspdk.sin300", function "coupler". <see cref="Parameters"/> is an optional
/// Python kwargs fragment (e.g. "length=10"). Renders in one of two ways depending on
/// <see cref="Backend"/>: nazca via module/function dispatch, gdsfactory via a raw-code
/// import-and-call wrapper (see <see cref="ToGdsFactoryRawCode"/>).
/// </summary>
/// <param name="Backend">Which geometry engine builds this component.</param>
/// <param name="Module">Python module path, e.g. "cspdk.sin300". Null/empty for gdsfactory's default import.</param>
/// <param name="Function">Cell-producing function name, e.g. "coupler".</param>
/// <param name="Parameters">Optional Python kwargs fragment passed to the function call.</param>
public sealed record GeometryReference(GeometryBackend Backend, string? Module, string Function, string? Parameters)
{
    /// <summary>The fully-qualified call, e.g. "cspdk.sin300.coupler" or "coupler".</summary>
    public string QualifiedFunction => string.IsNullOrWhiteSpace(Module) ? Function : $"{Module}.{Function}";

    /// <summary>
    /// Wraps the reference in a raw-code snippet the gdsfactory preview script understands:
    /// it imports the module and assigns the built cell to a variable named <c>component</c>.
    /// </summary>
    public string ToGdsFactoryRawCode()
    {
        var import = string.IsNullOrWhiteSpace(Module) ? "import gdsfactory as gf" : $"import {Module}";
        return $"{import}\ncomponent = {QualifiedFunction}({Parameters ?? string.Empty})";
    }
}
