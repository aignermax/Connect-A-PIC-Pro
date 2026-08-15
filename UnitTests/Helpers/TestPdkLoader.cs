using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper;

namespace UnitTests;

/// <summary>
/// Test helper that loads <see cref="ComponentTemplate"/> instances from the bundled
/// JSON PDK files (demo-pdk.json, siepic-ebeam-pdk.json).
///
/// Use this in tests instead of the obsolete <c>ComponentTemplates.GetAllTemplates()</c>.
/// </summary>
public static class TestPdkLoader
{
    /// <summary>
    /// Loads all component templates from the bundled JSON PDK files in the test
    /// output directory. Returns an empty list if the PDKs directory is not found.
    /// </summary>
    public static List<ComponentTemplate> LoadAllTemplates()
    {
        var pdkDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs");
        if (!Directory.Exists(pdkDir))
            return new List<ComponentTemplate>();

        var templates = new List<ComponentTemplate>();
        var loader = new PdkLoader();

        foreach (var file in Directory.GetFiles(pdkDir, "*.json").OrderBy(f => f))
        {
            try
            {
                var pdk = loader.LoadFromFile(file);
                foreach (var comp in pdk.Components)
                    templates.Add(PdkTemplateConverter.ConvertToTemplate(comp, pdk.Name, pdk.NazcaModuleName, process: pdk.Process));
            }
            catch
            {
                // Skip malformed PDK files in test environments
            }
        }

        return templates;
    }

    /// <summary>
    /// Loads templates from a single named PDK file (e.g. "demo-pdk.json").
    /// Returns an empty list if the file is not found.
    /// </summary>
    public static List<ComponentTemplate> LoadFromPdk(string pdkFileName)
    {
        var pdkDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs");
        var filePath = Path.Combine(pdkDir, pdkFileName);
        if (!File.Exists(filePath))
            return new List<ComponentTemplate>();

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(filePath);
        return pdk.Components
            .Select(c => PdkTemplateConverter.ConvertToTemplate(c, pdk.Name, pdk.NazcaModuleName, process: pdk.Process))
            .ToList();
    }
}
