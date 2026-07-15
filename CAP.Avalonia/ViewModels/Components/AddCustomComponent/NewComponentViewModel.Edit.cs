using System.Linq;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

public partial class NewComponentViewModel
{
    private string? _editingOriginalName;

    private string? _editOriginalPdkFilePath;
    private string? _editOriginalPdkName;
    private string? _editOriginalProcessName;

    public string? MigratedFromPdkName { get; private set; }

    public void LoadForEdit(ComponentTemplate template)
    {
        var match = PdkChoices.FirstOrDefault(c => !c.IsNewPdk && c.Pdk?.Name == template.PdkSource);
        if (match is null)
        {
            StatusText = $"Cannot edit '{template.Name}': its PDK '{template.PdkSource}' is not a custom PDK.";
            return;
        }

        ComponentName = template.Name;

        var backend = template.RawCodeBackend == "nazca" ? GeometryBackend.Nazca : GeometryBackend.GdsFactory;
        var code = template.RawCode ?? string.Empty;
        if (string.IsNullOrEmpty(code))
        {
            var synthesized = SynthesizeCodeFromReference(template);
            if (synthesized is { } s)
            {
                code = s.Code;
                backend = s.Backend;
                StatusText = "Loaded the foundry definition as editable code — adjust and save to fork it.";
            }
            else
            {
                StatusText = "No stored code for this component — enter code to edit.";
            }
        }
        SelectedBackend = backend;
        Code = code;
        SelectedPdkChoice = match;

        _editingOriginalName = template.Name;
        _editOriginalPdkFilePath = match.Pdk!.FilePath;
        _editOriginalPdkName = match.Pdk.Name;
        _editOriginalProcessName = match.Pdk.Process?.Name;
        IsEditMode = true;
    }

    /// <summary>
    /// Turns a foundry component's function reference into equivalent editable code, so a bundled
    /// component opens with a visible, runnable definition instead of a blank editor.
    /// </summary>
    private static (string Code, GeometryBackend Backend)? SynthesizeCodeFromReference(ComponentTemplate t)
    {
        if (!string.IsNullOrWhiteSpace(t.GdsFactoryFunction))
        {
            var top = t.GdsFactoryFunction.Split('.')[0];
            return ($"import {top}\ncomponent = {t.GdsFactoryFunction}()", GeometryBackend.GdsFactory);
        }
        if (!string.IsNullOrWhiteSpace(t.NazcaFunctionName))
        {
            var module = t.NazcaModuleName;
            var call = string.IsNullOrWhiteSpace(module) ? t.NazcaFunctionName : $"{module}.{t.NazcaFunctionName}";
            var import = string.IsNullOrWhiteSpace(module) ? "import nazca as nd" : $"import {module.Split('.')[0]}";
            return ($"{import}\ncomponent = {call}()", GeometryBackend.Nazca);
        }
        return null;
    }
}
