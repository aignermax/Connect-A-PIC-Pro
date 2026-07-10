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
    /// <summary>
    /// Verbatim Python cell code to render directly ("raw-code mode"). When set, this takes
    /// precedence over <see cref="Module"/>/<see cref="Function"/>/<see cref="Parameters"/>:
    /// <see cref="ComponentGeometryExtractor"/> passes it as-is to the backend-appropriate
    /// renderer's <c>RenderRawCodeAsync</c> — no import/wrapper synthesis, the user's code
    /// must define <c>component</c> itself. Named <c>RawSourceCode</c> rather than
    /// <c>RawCode</c> because the static factory below is named <see cref="RawCode"/>; a
    /// property and a method cannot share one identifier in C# (CS0102).
    /// </summary>
    public string? RawSourceCode { get; init; }

    /// <summary>The fully-qualified call, e.g. "cspdk.sin300.coupler" or "coupler".</summary>
    public string QualifiedFunction => string.IsNullOrWhiteSpace(Module) ? Function : $"{Module}.{Function}";

    /// <summary>
    /// Creates a raw-code geometry reference: <paramref name="code"/> is rendered verbatim by
    /// the <paramref name="backend"/>-appropriate renderer, bypassing module/function dispatch.
    /// </summary>
    /// <param name="backend">Which renderer (nazca or gdsfactory) executes <paramref name="code"/>.</param>
    /// <param name="code">Verbatim Python cell code; must assign a variable named <c>component</c>.</param>
    public static GeometryReference RawCode(GeometryBackend backend, string code) =>
        new(backend, null, string.Empty, null) { RawSourceCode = code };

    /// <summary>
    /// Wraps the reference in a raw-code snippet the gdsfactory preview script understands:
    /// it imports the module and assigns the built cell to a variable named <c>component</c>.
    /// </summary>
    /// <remarks>
    /// When <see cref="Module"/> is set, the emitted code is
    /// <c>import &lt;Module&gt;\ncomponent = &lt;Module&gt;.&lt;Function&gt;(&lt;Parameters&gt;)</c>.
    /// When <see cref="Module"/> is null/empty only <c>import gdsfactory as gf</c> is emitted,
    /// so <see cref="Function"/> MUST then be gf-qualified (e.g. "gf.components.straight");
    /// a bare name like "straight" would produce <c>component = straight()</c> with no import
    /// that resolves it, raising a Python <c>NameError</c>. A later UI task enforces/derives
    /// this qualification — here the contract is only documented, not validated.
    /// </remarks>
    public string ToGdsFactoryRawCode()
    {
        var import = string.IsNullOrWhiteSpace(Module) ? "import gdsfactory as gf" : $"import {Module}";
        return $"{import}\ncomponent = {QualifiedFunction}({Parameters ?? string.Empty})";
    }
}
