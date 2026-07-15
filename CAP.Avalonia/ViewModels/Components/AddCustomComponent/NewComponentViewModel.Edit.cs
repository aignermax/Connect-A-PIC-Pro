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
        SelectedBackend = template.RawCodeBackend == "nazca" ? GeometryBackend.Nazca : GeometryBackend.GdsFactory;
        Code = template.RawCode ?? string.Empty;
        if (string.IsNullOrEmpty(template.RawCode))
        {
            StatusText = "No stored code for this component — enter code to edit.";
        }
        SelectedPdkChoice = match;

        _editingOriginalName = template.Name;
        _editOriginalPdkFilePath = match.Pdk!.FilePath;
        _editOriginalPdkName = match.Pdk.Name;
        _editOriginalProcessName = match.Pdk.Process?.Name;
        IsEditMode = true;
    }
}
