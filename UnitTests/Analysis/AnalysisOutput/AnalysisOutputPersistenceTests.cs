using System.Text.Json;
using CAP.Avalonia.ViewModels;
using Shouldly;

namespace UnitTests.Analysis.AnalysisOutput;

/// <summary>
/// Verifies the .lun round-trip contract of the analysis-output designation (#754):
/// the designated coupler's Identifier survives save → reload, legacy files without
/// the field deserialize cleanly, and the JSON property name stays stable.
/// </summary>
public class AnalysisOutputPersistenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Roundtrip_WithDesignation_PreservesCouplerIdentifier()
    {
        var original = new DesignFileData
        {
            FormatVersion = "2.0",
            AnalysisOutputCoupler = "GratingCoupler_2",
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundtripped = JsonSerializer.Deserialize<DesignFileData>(json);

        roundtripped.ShouldNotBeNull();
        roundtripped!.AnalysisOutputCoupler.ShouldBe("GratingCoupler_2");
    }

    [Fact]
    public void LegacyFileWithoutField_DeserializesWithNullDesignation()
    {
        const string legacyJson = """
            {
              "FormatVersion": "2.0",
              "Components": [],
              "Connections": []
            }
            """;

        var data = JsonSerializer.Deserialize<DesignFileData>(legacyJson);

        data.ShouldNotBeNull();
        data!.AnalysisOutputCoupler.ShouldBeNull();
    }

    [Fact]
    public void UnknownFutureFields_AreIgnoredOnLoad()
    {
        // Symmetry of the "unknown field" contract: a file with extra fields (e.g. a
        // newer version's designation written next to unknown siblings) still loads.
        const string json = """
            {
              "FormatVersion": "2.0",
              "Components": [],
              "Connections": [],
              "AnalysisOutputCoupler": "GC_out",
              "SomeFutureField": { "nested": true }
            }
            """;

        var data = JsonSerializer.Deserialize<DesignFileData>(json);

        data.ShouldNotBeNull();
        data!.AnalysisOutputCoupler.ShouldBe("GC_out");
    }

    [Fact]
    public void Serialization_UsesStableJsonPropertyName()
    {
        var data = new DesignFileData { AnalysisOutputCoupler = "GC_1" };

        var json = JsonSerializer.Serialize(data, JsonOptions);

        // Pin the on-disk property name — renaming it would break existing user files.
        json.ShouldContain("\"AnalysisOutputCoupler\":\"GC_1\"");
    }

    [Fact]
    public void Serialization_OmitsFieldWhenNoDesignation()
    {
        var data = new DesignFileData();

        var json = JsonSerializer.Serialize(data, JsonOptions);

        json.ShouldNotContain("AnalysisOutputCoupler");
    }
}
