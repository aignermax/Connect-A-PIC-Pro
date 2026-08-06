using System.Text.Json;
using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper
{
    public class PdkLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public PdkDraft LoadFromFile(string filePath)
            => LoadFromFileInternal(filePath, requireNazcaOffset: true);

        public PdkDraft LoadFromFileForEditing(string filePath)
            => LoadFromFileInternal(filePath, requireNazcaOffset: false);

        private PdkDraft LoadFromFileInternal(string filePath, bool requireNazcaOffset)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"PDK file not found: {filePath}");
            }

            var json = File.ReadAllText(filePath);
            var pdk = LoadFromJson(json, requireNazcaOffset);
            pdk.FilePath = filePath;
            return pdk;
        }

        public PdkDraft LoadFromJson(string json)
            => LoadFromJson(json, requireNazcaOffset: true);

        private PdkDraft LoadFromJson(string json, bool requireNazcaOffset)
        {
            var pdk = JsonSerializer.Deserialize<PdkDraft>(json, JsonOptions);
            if (pdk == null)
            {
                throw new InvalidOperationException("Failed to deserialize PDK JSON");
            }

            ValidatePdk(pdk, requireNazcaOffset);
            return pdk;
        }

        public List<PdkDraft> LoadFromDirectory(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException($"PDK directory not found: {directoryPath}");
            }

            var pdks = new List<PdkDraft>();
            var jsonFiles = Directory.GetFiles(directoryPath, "*.json");

            foreach (var file in jsonFiles)
            {
                try
                {
                    var pdk = LoadFromFile(file);
                    pdks.Add(pdk);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to load PDK from {file}: {ex.Message}");
                }
            }

            return pdks;
        }

        private void ValidatePdk(PdkDraft pdk, bool requireNazcaOffset)
        {
            if (string.IsNullOrWhiteSpace(pdk.Name))
            {
                throw new InvalidOperationException("PDK must have a name");
            }

            var errors = new List<string>();
            foreach (var comp in pdk.Components)
            {
                ValidateComponent(comp, pdk.Name, errors, requireNazcaOffset, pdk.IsGdsFactoryBackend);
            }

            if (errors.Count > 0)
            {
                throw new PdkValidationException(pdk.Name, errors);
            }
        }

        private static void ValidateComponent(PdkComponentDraft comp, string pdkName, List<string> errors, bool requireNazcaOffset, bool pdkIsGdsFactory)
        {
            var compLabel = !string.IsNullOrWhiteSpace(comp.Name) ? comp.Name : "(unnamed)";

            bool isAnalysisTool = comp.NazcaFunction == "__analyzer__";

            bool isGdsFactoryComponent = !string.IsNullOrWhiteSpace(comp.GdsFactoryFunction);

            bool hasRawCode = !string.IsNullOrWhiteSpace(comp.RawCode);

            if (pdkIsGdsFactory && !isAnalysisTool && !isGdsFactoryComponent && !hasRawCode
                && string.IsNullOrWhiteSpace(comp.NazcaFunction))
            {
                errors.Add($"[{pdkName}/{compLabel}] gdsfactory-backend component must declare a gdsFactoryFunction (or a nazcaFunction)");
            }

            if (string.IsNullOrWhiteSpace(comp.Name))
            {
                errors.Add($"[{pdkName}] Component must have a name");
            }

            if (comp.WidthMicrometers <= 0)
            {
                errors.Add($"[{pdkName}/{compLabel}] Width must be positive");
            }

            if (comp.HeightMicrometers <= 0)
            {
                errors.Add($"[{pdkName}/{compLabel}] Height must be positive");
            }

            if ((comp.Pins == null || comp.Pins.Count == 0)
                && (comp.OutlinePolygons == null || comp.OutlinePolygons.Count == 0))
            {
                errors.Add($"[{pdkName}/{compLabel}] Must have at least one pin " +
                           "(or outline polygons for a geometry-only component)");
            }

            if (requireNazcaOffset && !isAnalysisTool && !isGdsFactoryComponent && !hasRawCode)
            {
                if (comp.NazcaOriginOffsetX == null)
                {
                    errors.Add($"[{pdkName}/{compLabel}] Missing nazcaOriginOffsetX (required for GDS export)");
                }
                if (comp.NazcaOriginOffsetY == null)
                {
                    errors.Add($"[{pdkName}/{compLabel}] Missing nazcaOriginOffsetY (required for GDS export)");
                }
            }

            if (comp.Pins != null)
            {
                foreach (var pin in comp.Pins)
                {
                    if (string.IsNullOrWhiteSpace(pin.Name))
                    {
                        errors.Add($"[{pdkName}/{compLabel}] Pin must have a name");
                    }

                    if (!CAP_Core.Components.PinKinds.PinKindHelper.TryParse(pin.PinKind, out _))
                    {
                        errors.Add($"[{pdkName}/{compLabel}] Pin '{pin.Name}' has invalid pinKind '{pin.PinKind}' (expected 'Optical' or 'Electrical')");
                    }

                    const double tolerance = 1.0;
                    if (pin.OffsetXMicrometers < -tolerance || pin.OffsetXMicrometers > comp.WidthMicrometers + tolerance)
                    {
                        errors.Add($"[{pdkName}/{compLabel}] Pin '{pin.Name}' X={pin.OffsetXMicrometers} outside bounds [0, {comp.WidthMicrometers}]");
                    }
                    if (pin.OffsetYMicrometers < -tolerance || pin.OffsetYMicrometers > comp.HeightMicrometers + tolerance)
                    {
                        errors.Add($"[{pdkName}/{compLabel}] Pin '{pin.Name}' Y={pin.OffsetYMicrometers} outside bounds [0, {comp.HeightMicrometers}]");
                    }

                    if (!CAP_Core.Components.Core.PolarizationRules.TryParse(pin.Polarization, out _))
                    {
                        errors.Add($"[{pdkName}/{compLabel}] Pin '{pin.Name}' has invalid polarization '{pin.Polarization}' (expected TE, TM or Both)");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(comp.CompactModel) &&
                !CompactModelRegistry.IsRegistered(comp.CompactModel))
            {
                errors.Add(
                    $"[{pdkName}/{compLabel}] Unknown compactModel '{comp.CompactModel}'. " +
                    $"Known models: {string.Join(", ", CompactModelRegistry.RegisteredNames)}");
            }

            if (comp.SMatrix != null && ParametricSMatrixMapper.IsParametric(comp.SMatrix))
            {
                ParametricSMatrixMapper.Validate(comp.SMatrix, comp.Name, comp.Pins, comp.Sliders?.Count ?? 0);
            }
        }
    }
}
