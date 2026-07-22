namespace CAP.Avalonia.Services.AddCustomComponent;

public enum GeometryBackend
{
    Nazca,

    GdsFactory,
}

public sealed record GeometryReference(GeometryBackend Backend, string? Module, string Function, string? Parameters)
{
    public string? RawSourceCode { get; init; }

    public string QualifiedFunction => string.IsNullOrWhiteSpace(Module) ? Function : $"{Module}.{Function}";

    public static GeometryReference RawCode(GeometryBackend backend, string code) =>
        new(backend, null, string.Empty, null) { RawSourceCode = code };

    public string ToGdsFactoryRawCode()
    {
        var import = string.IsNullOrWhiteSpace(Module) ? "import gdsfactory as gf" : $"import {Module}";
        return $"{import}\ncomponent = {QualifiedFunction}({Parameters ?? string.Empty})";
    }
}
