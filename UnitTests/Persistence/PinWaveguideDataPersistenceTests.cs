using System.Text.Json;
using CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.Grid;
using CAP_Core.Helpers;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// Persistence round-trips for the PDK-sourced pin waveguide width/layer
/// (issue #906): the values survive the PDK JSON saver/loader, group template
/// serialization, pin-override capture/apply, group cloning, and .lun save/load.
/// Pins without data keep null everywhere, so legacy files stay byte-compatible
/// and the pin-mismatch rule stays silent for them.
/// </summary>
public class PinWaveguideDataPersistenceTests
{
    [Fact]
    public void PhysicalPinDraft_WidthAndLayer_RoundtripThroughPdkSaverAndLoader()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lunima-pindata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "p.json");
        var pdk = new PdkDraft
        {
            Name = "My P",
            Components = new()
            {
                new PdkComponentDraft
                {
                    Name = "My Cell", WidthMicrometers = 10, HeightMicrometers = 2,
                    Pins = new()
                    {
                        new PhysicalPinDraft { Name = "o1", WaveguideWidthMicrometers = 0.5, Layer = 1 },
                        new PhysicalPinDraft { Name = "o2" },
                    }
                }
            }
        };

        new PdkJsonSaver().SaveToFile(pdk, path);
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);

        reloaded.Components[0].Pins[0].WaveguideWidthMicrometers.ShouldBe(0.5);
        reloaded.Components[0].Pins[0].Layer.ShouldBe(1);
        reloaded.Components[0].Pins[1].WaveguideWidthMicrometers.ShouldBeNull();
        reloaded.Components[0].Pins[1].Layer.ShouldBeNull();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void OverridePinData_WidthAndLayer_SurviveCaptureApplyAndSerialization()
    {
        var component = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        foreach (var pin in component.PhysicalPins)
        {
            pin.WaveguideWidthMicrometers = 0.5;
            pin.Layer = 1;
        }

        var captured = OverridePinMapper.CaptureAsPinData(component.PhysicalPins);
        var json = JsonSerializer.Serialize(captured);
        var restored = JsonSerializer.Deserialize<List<OverridePinData>>(json)!;

        OverridePinMapper.ApplyPinsToComponent(component, restored);

        component.PhysicalPins.ShouldAllBe(p => p.WaveguideWidthMicrometers == 0.5 && p.Layer == 1);
    }

    [Fact]
    public void GroupTemplateSerializer_Roundtrip_PreservesPinWidthAndLayer()
    {
        var child = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        foreach (var pin in child.PhysicalPins)
        {
            pin.WaveguideWidthMicrometers = 1.2;
            pin.Layer = 203;
        }
        var group = new ComponentGroup("g");
        group.AddChild(child);

        var restored = GroupTemplateSerializer.Deserialize(GroupTemplateSerializer.Serialize(group))!;

        restored.ChildComponents[0].PhysicalPins
            .ShouldAllBe(p => p.WaveguideWidthMicrometers == 1.2 && p.Layer == 203);
    }

    [Fact]
    public void GroupClone_PreservesChildPinWidthAndLayer()
    {
        var child = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        foreach (var pin in child.PhysicalPins)
        {
            pin.WaveguideWidthMicrometers = 0.5;
            pin.Layer = 1;
        }
        var group = new ComponentGroup("g");
        group.AddChild(child);

        var clone = (ComponentGroup)group.Clone();

        clone.ChildComponents[0].PhysicalPins
            .ShouldAllBe(p => p.WaveguideWidthMicrometers == 0.5 && p.Layer == 1);
    }

    [Fact]
    public async Task LunSaveLoad_PreservesPinWidthAndLayer_ViaTemplateRecreation()
    {
        var placed = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        foreach (var pin in placed.PhysicalPins)
        {
            pin.WaveguideWidthMicrometers = 0.5;
            pin.Layer = 1;
        }
        var grid = new GridManager(24, 12);
        grid.ComponentMover.PlaceComponent(0, 5, placed);

        var draft = (Component)placed.Clone();
        var componentFactory = new ComponentFactory();
        componentFactory.InitializeComponentDrafts(new List<Component> { draft });

        var persistence = new GridPersistenceManager(grid, new FileDataAccessor());
        var tempSavePath = Path.GetTempFileName();
        await persistence.SaveAsync(tempSavePath);
        await persistence.LoadAsync(tempSavePath, componentFactory);

        var loaded = grid.ComponentMover.GetComponentAt(0, 5);
        loaded.PhysicalPins.ShouldNotBeEmpty();
        loaded.PhysicalPins.ShouldAllBe(p => p.WaveguideWidthMicrometers == 0.5 && p.Layer == 1);
    }
}
