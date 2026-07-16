using System.Numerics;
using CAP.Avalonia.Services;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using UnitTests;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Tests for <see cref="SMatrixOverrideApplicator"/>.
/// Verifies that stored S-matrix data is applied correctly to live components.
/// </summary>
public class SMatrixOverrideApplicatorTests
{
    private static ComponentSMatrixData MakeData(string wavelengthKey, int portCount)
    {
        int n = portCount;
        var real = new List<double>();
        var imag = new List<double>();

        for (int i = 0; i < n * n; i++)
        {
            real.Add(i / n == i % n ? 0.9 : 0.05);
            imag.Add(0.0);
        }

        var data = new ComponentSMatrixData { SourceNote = "Test" };
        data.Wavelengths[wavelengthKey] = new SMatrixWavelengthEntry
        {
            Rows = n,
            Cols = n,
            Real = real,
            Imag = imag
        };

        return data;
    }

    [Fact]
    public void Apply_ValidData_ReturnsAppliedCount()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = MakeData("1550", 2);

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(1);
        result.Skipped.Count.ShouldBe(0);
        component.WaveLengthToSMatrixMap.ShouldContainKey(1550);
    }

    [Fact]
    public void Apply_ReplacingExistingWavelength_CountsAsReplaced()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        // CreateSimpleTwoPortComponent seeds the map with Red/Green/Blue standard wavelengths;
        // pick one of those so we can verify the Replaced counter.
        int existingKey = component.WaveLengthToSMatrixMap.Keys.First();
        var data = MakeData(existingKey.ToString(), 2);

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(1);
        result.Replaced.ShouldBe(1);
    }

    [Fact]
    public void Apply_NonSquareEntry_IsSkippedWithReason()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = new ComponentSMatrixData();
        data.Wavelengths["1550"] = new SMatrixWavelengthEntry
        {
            Rows = 2,
            Cols = 3,
            Real = new List<double>(new double[6]),
            Imag = new List<double>(new double[6])
        };

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(0);
        result.IsTotalFailure.ShouldBeTrue();
        result.Skipped[0].Reason.ShouldContain("not square");
    }

    [Fact]
    public void Apply_ComponentWithNoPhysicalPins_SkipsAllWithReason()
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.PhysicalPins.Count.ShouldBe(0);
        var data = MakeData("1550", 2);

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(0);
        result.Skipped.Count.ShouldBe(1);
        result.Skipped[0].Reason.ShouldContain("no physical pins");
    }

    [Fact]
    public void Apply_MalformedRealArray_IsSkippedWithReason()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = new ComponentSMatrixData();
        data.Wavelengths["1550"] = new SMatrixWavelengthEntry
        {
            Rows = 2,
            Cols = 2,
            Real = new List<double> { 0.9 },
            Imag = new List<double>(new double[4])
        };

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(0);
        result.Skipped[0].Reason.ShouldContain("shorter than 4");
    }

    [Fact]
    public void Apply_NonIntegerWavelengthKey_IsSkipped()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = new ComponentSMatrixData();
        data.Wavelengths["1.55e-6"] = new SMatrixWavelengthEntry
        {
            Rows = 2, Cols = 2,
            Real = new List<double> { 0.9, 0.1, 0.1, 0.9 },
            Imag = new List<double>(new double[4])
        };

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(0);
        result.Skipped[0].Reason.ShouldContain("not an integer");
    }

    [Fact]
    public void Apply_DimensionExceedsPinCount_IsSkipped()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = new ComponentSMatrixData();
        data.Wavelengths["1550"] = new SMatrixWavelengthEntry
        {
            Rows = 3, Cols = 3,
            Real = new List<double>(new double[9]),
            Imag = new List<double>(new double[9])
        };

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(0);
        result.Skipped[0].Reason.ShouldContain("usable pins");
    }

    [Fact]
    public void Apply_DimensionLessThanPinCount_PositionalPathRejectsSilentTruncation()
    {
        // Symmetric to Apply_DimensionExceedsPinCount_IsSkipped: a 2-pin
        // component with a 1×1 import (no PortNames). Without explicit
        // mapping, the only safe answer is "fail loud" — silently keeping
        // the first pin would replace the wavelength's whole SMatrix with
        // a 1×1 and orphan the second pin for that wavelength (the
        // simulator would route light through a port that no longer
        // exists in this wavelength's map).
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = MakeData("1550", portCount: 1);

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(0);
        result.Skipped.Count.ShouldBe(1);
        result.Skipped[0].Reason.ShouldContain("PortNames",
            customMessage: "skip message must point the user at PortNames as the unambiguous fix");
    }

    [Fact]
    public void Apply_TwoWavelengths_BothApplied()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = MakeData("1310", 2);
        data.Wavelengths["1550"] = data.Wavelengths["1310"];   // share the same matrix

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(2);
        component.WaveLengthToSMatrixMap.ShouldContainKey(1310);
        component.WaveLengthToSMatrixMap.ShouldContainKey(1550);
    }

    [Fact]
    public void Apply_PortNamesCountMismatch_HardFails()
    {
        // Port names provided but with wrong count must NOT silently fall back to positional —
        // a misaligned name list should surface as a hard skip, not as physically reordered S-matrix.
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = new ComponentSMatrixData();
        data.Wavelengths["1550"] = new SMatrixWavelengthEntry
        {
            Rows = 2, Cols = 2,
            Real = new List<double> { 0.9, 0.1, 0.1, 0.9 },
            Imag = new List<double>(new double[4]),
            PortNames = new List<string> { "in", "out", "extra" }
        };

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(0);
        result.Skipped[0].Reason.ShouldContain("PortNames count");
    }

    [Fact]
    public void Apply_PortNameNotFoundOnComponent_HardFailsWithAvailableNames()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = new ComponentSMatrixData();
        data.Wavelengths["1550"] = new SMatrixWavelengthEntry
        {
            Rows = 2, Cols = 2,
            Real = new List<double> { 0.9, 0.1, 0.1, 0.9 },
            Imag = new List<double>(new double[4]),
            PortNames = new List<string> { "TE0", "TE1" }   // valid count, none match
        };

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(0);
        result.Skipped[0].Reason.ShouldContain("not found");
        result.Skipped[0].Reason.ShouldContain("in");
        result.Skipped[0].Reason.ShouldContain("out");
    }

    [Fact]
    public void Apply_NullPortNames_UsesPositionalOrder()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = new ComponentSMatrixData();
        data.Wavelengths["1550"] = new SMatrixWavelengthEntry
        {
            Rows = 2, Cols = 2,
            Real = new List<double> { 0.9, 0.1, 0.1, 0.9 },
            Imag = new List<double>(new double[4]),
            PortNames = null
        };

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(1);
    }

    [Fact]
    public void ApplyAll_OnlyOverridesMatchingComponent_ReportsNoOrphans()
    {
        var comp1 = TestComponentFactory.CreateSimpleTwoPortComponent();
        comp1.Identifier = "comp_1";
        var comp2 = TestComponentFactory.CreateSimpleTwoPortComponent();
        comp2.Identifier = "comp_2";

        var comp2OriginalKeys = comp2.WaveLengthToSMatrixMap.Keys.ToHashSet();

        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["comp_1"] = MakeData("1550", 2)
        };

        var result = SMatrixOverrideApplicator.ApplyAll(new[] { comp1, comp2 }, store);

        comp1.WaveLengthToSMatrixMap.ShouldContainKey(1550);
        comp2.WaveLengthToSMatrixMap.Keys.ToHashSet().ShouldBe(comp2OriginalKeys);
        result.PerComponent["comp_1"].Applied.ShouldBe(1);
        result.OrphanKeys.ShouldBeEmpty();
    }

    [Fact]
    public void ApplyAll_StoreContainsKeyForRemovedComponent_ReportsAsOrphan()
    {
        var comp1 = TestComponentFactory.CreateSimpleTwoPortComponent();
        comp1.Identifier = "comp_1";

        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["comp_1"] = MakeData("1550", 2),
            ["deleted_comp"] = MakeData("1550", 2)   // no live component for this key
        };

        var result = SMatrixOverrideApplicator.ApplyAll(new[] { comp1 }, store);

        result.PerComponent["comp_1"].Applied.ShouldBe(1);
        result.OrphanKeys.ShouldContain("deleted_comp");
        result.OrphanKeys.Count.ShouldBe(1);
    }

    [Fact]
    public void ApplyAll_TemplateOverrideExistsButNotPlaced_NotReportedAsOrphan()
    {
        // Regression guard: a "Demo PDK::2x2 MMI Coupler" override in the user
        // store is NOT orphan just because that template isn't placed on the
        // current canvas — the override will still apply on the next placement.
        // Without keyMatchesKnownTemplate, the applicator logged a misleading
        // warning every time the project was loaded or simulated.
        var someInstance = TestComponentFactory.CreateSimpleTwoPortComponent();
        someInstance.Identifier = "instance_only";

        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["Demo PDK::2x2 MMI Coupler"] = MakeData("1550", 2),  // matches a library template
            ["genuinely_gone"] = MakeData("1550", 2)               // matches nothing
        };

        var result = SMatrixOverrideApplicator.ApplyAll(
            new[] { someInstance },
            store,
            keyMatchesKnownTemplate: key => key == "Demo PDK::2x2 MMI Coupler");

        // The library-template-keyed override is "deferred", not orphan.
        result.OrphanKeys.ShouldNotContain("Demo PDK::2x2 MMI Coupler");
        // The truly-gone one is still reported.
        result.OrphanKeys.ShouldContain("genuinely_gone");
        result.OrphanKeys.Count.ShouldBe(1);
    }

    [Fact]
    public void ApplyAll_WithoutTemplatePredicate_PreservesOriginalOrphanBehavior()
    {
        // Backwards-compat: callers that don't supply keyMatchesKnownTemplate
        // get the old behaviour — every unmatched key is reported.
        var someInstance = TestComponentFactory.CreateSimpleTwoPortComponent();
        someInstance.Identifier = "instance_only";

        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["unmatched_key"] = MakeData("1550", 2)
        };

        var result = SMatrixOverrideApplicator.ApplyAll(new[] { someInstance }, store);

        result.OrphanKeys.ShouldContain("unmatched_key");
    }

    [Fact]
    public void ApplyAll_TemplateKeyResolver_AppliesPdkTemplateOverride()
    {
        // PDK template overrides are stored under "{pdkSource}::{templateName}" but
        // newly placed instances get a unique Identifier. The optional templateKeyResolver
        // bridges the two so the override reaches every instance of the template.
        var comp = TestComponentFactory.CreateSimpleTwoPortComponent();
        comp.Identifier = "instance_42";

        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["siepic::straight_wg"] = MakeData("1550", 2)
        };

        var result = SMatrixOverrideApplicator.ApplyAll(
            new[] { comp },
            store,
            templateKeyResolver: _ => "siepic::straight_wg");

        result.PerComponent["instance_42"].Applied.ShouldBe(1);
        result.OrphanKeys.ShouldBeEmpty();
        comp.WaveLengthToSMatrixMap.ShouldContainKey(1550);
    }

    [Fact]
    public void Apply_AsymmetricMatrix_PreservesRowColAndInOutFlowConvention()
    {
        // Convention pinned by this test (must NOT be relaxed):
        //   entry.Real[row*n + col] = S[row, col] = transmission from port col → port row
        //   maps to transfers[(pins[col].IDInFlow, pins[row].IDOutFlow)] = value
        // A row↔col swap, an InFlow↔OutFlow swap, or a row-major↔col-major flatten
        // would each silently corrupt physics; this test catches all three.
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var pinIn = component.PhysicalPins[0].LogicalPin!;
        var pinOut = component.PhysicalPins[1].LogicalPin!;

        var data = new ComponentSMatrixData();
        data.Wavelengths["1550"] = new SMatrixWavelengthEntry
        {
            Rows = 2, Cols = 2,
            Real = new List<double> { 1, 2, 3, 4 },
            Imag = new List<double>(new double[4])
        };

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(1);
        var transfers = component.WaveLengthToSMatrixMap[1550].GetNonNullValues();
        transfers[(pinIn.IDInFlow, pinIn.IDOutFlow)].ShouldBe(new Complex(1, 0));
        transfers[(pinOut.IDInFlow, pinIn.IDOutFlow)].ShouldBe(new Complex(2, 0));
        transfers[(pinIn.IDInFlow, pinOut.IDOutFlow)].ShouldBe(new Complex(3, 0));
        transfers[(pinOut.IDInFlow, pinOut.IDOutFlow)].ShouldBe(new Complex(4, 0));
    }

    [Fact]
    public void Apply_WithPortNamesThatMatchPhysicalPins_Succeeds()
    {
        var component = TestComponentFactory.CreateSimpleTwoPortComponent();
        var data = new ComponentSMatrixData();
        data.Wavelengths["1550"] = new SMatrixWavelengthEntry
        {
            Rows = 2, Cols = 2,
            Real = new List<double> { 0.9, 0.1, 0.1, 0.9 },
            Imag = new List<double>(new double[4]),
            PortNames = new List<string> { "in", "out" }
        };

        var result = SMatrixOverrideApplicator.Apply(component, data);

        result.Applied.ShouldBe(1);
        component.WaveLengthToSMatrixMap.ShouldContainKey(1550);
    }

    [Fact]
    public void ApplyAll_DefaultReportOrphansFalse_DoesNotWarnConsole()
    {
        // Subset calls (e.g. the incremental "components added" handler) must not
        // emit the orphan warning: an unmatched key may match a component outside
        // the subset, and re-warning on every add was the duplicate-warning spam.
        var someInstance = TestComponentFactory.CreateSimpleTwoPortComponent();
        someInstance.Identifier = "instance_only";
        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["renamed_or_removed"] = MakeData("1550", 2)
        };
        var console = new ErrorConsoleService();

        var result = SMatrixOverrideApplicator.ApplyAll(
            new[] { someInstance }, store, errorConsole: console);

        console.Entries.ShouldBeEmpty();          // no user-visible warning
        result.OrphanKeys.ShouldContain("renamed_or_removed"); // still computed for callers
    }

    [Fact]
    public void ApplyAll_ReportOrphansTrue_WarnsConsoleOnce()
    {
        // The authoritative full-canvas call (project load) opts in and surfaces
        // genuinely unmatched overrides exactly once.
        var someInstance = TestComponentFactory.CreateSimpleTwoPortComponent();
        someInstance.Identifier = "instance_only";
        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["renamed_or_removed"] = MakeData("1550", 2)
        };
        var console = new ErrorConsoleService();

        SMatrixOverrideApplicator.ApplyAll(
            new[] { someInstance }, store, errorConsole: console, reportOrphans: true);

        console.Entries.Count.ShouldBe(1);
        console.Entries[0].Message.ShouldContain("renamed_or_removed");
    }

    [Fact]
    public void ApplyAll_GeometryKeyResolver_AppliesToAllMatchingInstances()
    {
        var a = TestComponentFactory.CreateSimpleTwoPortComponent(); a.Identifier = "inst_A";
        var b = TestComponentFactory.CreateSimpleTwoPortComponent(); b.Identifier = "inst_B";
        var store = new Dictionary<string, ComponentSMatrixData> { ["geo:v1-shared"] = MakeData("1550", 2) };

        var result = SMatrixOverrideApplicator.ApplyAll(
            new[] { a, b }, store,
            geometryKeyResolver: _ => "geo:v1-shared");

        result.PerComponent["inst_A"].Applied.ShouldBe(1);
        result.PerComponent["inst_B"].Applied.ShouldBe(1);
        result.OrphanKeys.ShouldBeEmpty();
    }

    [Fact]
    public void ApplyAll_GeometryKeyResolver_DifferentGeometryDoesNotInherit()
    {
        var a = TestComponentFactory.CreateSimpleTwoPortComponent(); a.Identifier = "inst_A";
        var b = TestComponentFactory.CreateSimpleTwoPortComponent(); b.Identifier = "inst_B";
        var store = new Dictionary<string, ComponentSMatrixData> { ["geo:v1-A"] = MakeData("1550", 2) };

        var result = SMatrixOverrideApplicator.ApplyAll(
            new[] { a, b }, store,
            geometryKeyResolver: c => c.Identifier == "inst_A" ? "geo:v1-A" : "geo:v1-B");

        result.PerComponent.ContainsKey("inst_A").ShouldBeTrue();
        result.PerComponent.ContainsKey("inst_B").ShouldBeFalse();
    }

    [Fact]
    public void CopyScenario_OverrideStoredByGeometry_AppliesToClone()
    {
        var a = TestComponentFactory.CreateSimpleTwoPortComponent(); a.Identifier = "A";
        a.NazcaFunctionName = "ebeam_edge_coupler";
        var b = TestComponentFactory.CreateSimpleTwoPortComponent(); b.Identifier = "B";
        b.NazcaFunctionName = "ebeam_edge_coupler";

        string GeoKey(Component c) => CAP.Avalonia.Services.ComponentGeometryKey.For(c);
        var store = new Dictionary<string, ComponentSMatrixData> { [GeoKey(a)] = MakeData("1550", 2) };

        var result = SMatrixOverrideApplicator.ApplyAll(
            new[] { a, b }, store, geometryKeyResolver: GeoKey);

        result.PerComponent["A"].Applied.ShouldBe(1);
        result.PerComponent["B"].Applied.ShouldBe(1);
    }
}
