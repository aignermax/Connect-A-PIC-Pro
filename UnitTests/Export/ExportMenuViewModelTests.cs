using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Export.Formats;
using CommunityToolkit.Mvvm.Input;
using Moq;
using Shouldly;
using UnitTests.Helpers;

namespace UnitTests.Export;

/// <summary>
/// Tests for <see cref="ExportMenuViewModel"/> and <see cref="IExportFormat"/> adapters.
/// </summary>
public class ExportMenuViewModelTests
{
    /// <summary>Minimal stub for IExportFormat used in registry tests.</summary>
    private sealed class StubFormat : IExportFormat
    {
        public string Name { get; }
        public string Icon => "🔧";
        public string Description => "Stub format";
        public string Background => "#3d3d3d";
        public IAsyncRelayCommand ExportCommand { get; }

        public StubFormat(string name)
        {
            Name = name;
            ExportCommand = new AsyncRelayCommand(() => Task.CompletedTask);
        }
    }

    [Fact]
    public void Constructor_StoresFormatsInOrder()
    {
        var f1 = new StubFormat("Alpha");
        var f2 = new StubFormat("Beta");
        var f3 = new StubFormat("Gamma");

        var vm = new ExportMenuViewModel(new[] { f1, f2, f3 });

        vm.Formats.Count.ShouldBe(3);
        vm.Formats[0].Name.ShouldBe("Alpha");
        vm.Formats[1].Name.ShouldBe("Beta");
        vm.Formats[2].Name.ShouldBe("Gamma");
    }

    [Fact]
    public void Constructor_AcceptsEmptyCollection()
    {
        var vm = new ExportMenuViewModel(Array.Empty<IExportFormat>());
        vm.Formats.ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_NullCollection_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new ExportMenuViewModel(null!));
    }

    [Fact]
    public void Constructor_NullEntry_Throws()
    {
        var formats = new IExportFormat?[] { new StubFormat("Alpha"), null, new StubFormat("Gamma") };

        var ex = Should.Throw<ArgumentException>(() => new ExportMenuViewModel(formats!));
        ex.Message.ShouldContain("index 1");
    }

    [Fact]
    public void NazcaExportFormat_HasCorrectMetadata()
    {
        var cmd = new AsyncRelayCommand(() => Task.CompletedTask);
        var format = new NazcaExportFormat(cmd);

        format.Name.ShouldBe("Nazca Python + GDS");
        format.Icon.ShouldBe("🐍");
        format.Description.ShouldNotBeNullOrEmpty();
        format.Background.ShouldNotBeNullOrEmpty();
        format.ExportCommand.ShouldBeSameAs(cmd);
    }

    [Fact]
    public void SaxExportFormat_HasCorrectMetadata()
    {
        var cmd = new AsyncRelayCommand(() => Task.CompletedTask);
        var format = new SaxExportFormat(cmd);

        format.Name.ShouldBe("SAX (Simphony)");
        format.Icon.ShouldBe("🌊");
        format.Description.ShouldNotBeNullOrEmpty();
        format.ExportCommand.ShouldBeSameAs(cmd);
    }

    [Fact]
    public void PhotonTorchExportFormat_HasCorrectMetadata()
    {
        var format = new PhotonTorchExportFormat();

        format.Name.ShouldBe("PhotonTorch");
        format.Icon.ShouldBe("🔦");
        format.Description.ShouldNotBeNullOrEmpty();
        format.ExportCommand.ShouldNotBeNull();
    }

    [Fact]
    public void VerilogAExportFormat_HasCorrectMetadata()
    {
        var verilogAVm = FormatAdapterTestHelpers.CreateVerilogAExportVm();
        var format = new VerilogAExportFormat(verilogAVm);

        format.Name.ShouldBe("Verilog-A / SPICE");
        format.Icon.ShouldBe("📊");
        format.Description.ShouldNotBeNullOrEmpty();
        format.ExportCommand.ShouldNotBeNull();
    }

    [Fact]
    public void VerilogAExportFormat_NullVm_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new VerilogAExportFormat(null!));
    }

    [Fact]
    public async Task PhotonTorchExportFormat_WithoutDialogWired_Throws()
    {
        var format = new PhotonTorchExportFormat();

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await format.ExportCommand.ExecuteAsync(null));
    }

    [Fact]
    public async Task VerilogAExportFormat_WithoutDialogWired_Throws()
    {
        var verilogAVm = FormatAdapterTestHelpers.CreateVerilogAExportVm();
        var format = new VerilogAExportFormat(verilogAVm);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await format.ExportCommand.ExecuteAsync(null));
    }

    [Fact]
    public async Task PhotonTorchExportFormat_InvokesDialogCallbackExactlyOnce()
    {
        var format = new PhotonTorchExportFormat();
        var invocationCount = 0;
        format.ShowOptionsDialogAsync = () =>
        {
            invocationCount++;
            return Task.CompletedTask;
        };

        await format.ExportCommand.ExecuteAsync(null);

        invocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task VerilogAExportFormat_InvokesDialogCallbackExactlyOnce()
    {
        var verilogAVm = FormatAdapterTestHelpers.CreateVerilogAExportVm();
        var format = new VerilogAExportFormat(verilogAVm);
        var invocationCount = 0;
        format.ShowOptionsDialogAsync = () =>
        {
            invocationCount++;
            return Task.CompletedTask;
        };

        await format.ExportCommand.ExecuteAsync(null);

        invocationCount.ShouldBe(1);
    }

    [Fact]
    public void VerilogAExportFormat_ExposesOptionsViewModel()
    {
        var verilogAVm = FormatAdapterTestHelpers.CreateVerilogAExportVm();
        var format = new VerilogAExportFormat(verilogAVm);

        format.OptionsViewModel.ShouldBeSameAs(verilogAVm);
    }

    [Fact]
    public void ExportMenu_ContainsAllFourFormats()
    {
        var cmd = new AsyncRelayCommand(() => Task.CompletedTask);
        var verilogAVm = FormatAdapterTestHelpers.CreateVerilogAExportVm();

        var formats = new IExportFormat[]
        {
            new NazcaExportFormat(cmd),
            new SaxExportFormat(cmd),
            new PhotonTorchExportFormat(),
            new VerilogAExportFormat(verilogAVm),
        };

        var menu = new ExportMenuViewModel(formats);

        menu.Formats.Count.ShouldBe(4);
        menu.Formats.Select(f => f.Name).ShouldContain("Nazca Python + GDS");
        menu.Formats.Select(f => f.Name).ShouldContain("SAX (Simphony)");
        menu.Formats.Select(f => f.Name).ShouldContain("PhotonTorch");
        menu.Formats.Select(f => f.Name).ShouldContain("Verilog-A / SPICE");
    }

    [Fact]
    public void MainViewModel_ExportMenu_HasFiveFormatsInExpectedOrder()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();

        vm.ExportMenu.Formats.Count.ShouldBe(5);
        vm.ExportMenu.Formats.Select(f => f.Name).ShouldBe(new[]
        {
            "Nazca Python + GDS",
            "gdsfactory",
            "SAX (Simphony)",
            "PhotonTorch",
            "Verilog-A / SPICE",
        });
    }
}

/// <summary>
/// Factory helpers shared across format adapter tests.
/// </summary>
internal static class FormatAdapterTestHelpers
{
    internal static CAP.Avalonia.ViewModels.Export.VerilogAExportViewModel CreateVerilogAExportVm()
    {
        var exporterMock = new Mock<CAP_Core.Export.VerilogAExporter>(MockBehavior.Loose);
        var writerMock = new Mock<CAP.Avalonia.Services.VerilogAFileWriter>(MockBehavior.Loose);
        var canvasMock = new Mock<CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel>(MockBehavior.Loose);
        return new CAP.Avalonia.ViewModels.Export.VerilogAExportViewModel(
            exporterMock.Object,
            writerMock.Object,
            canvasMock.Object);
    }
}
