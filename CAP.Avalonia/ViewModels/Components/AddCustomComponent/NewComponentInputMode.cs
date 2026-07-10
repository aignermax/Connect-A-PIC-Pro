namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>Which authoring mode <see cref="NewComponentViewModel"/> currently uses.</summary>
public enum NewComponentInputMode
{
    /// <summary>Module/function reference dispatch (v1 default).</summary>
    Reference,

    /// <summary>User-pasted or file-loaded raw Python code, rendered verbatim.</summary>
    OwnCode,
}
