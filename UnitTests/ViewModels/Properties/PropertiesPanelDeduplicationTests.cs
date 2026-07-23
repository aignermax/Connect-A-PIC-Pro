using System;
using System.IO;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Properties
{
    /// <summary>
    /// Regression guards for issue #554: the hard-coded "Laser Configuration"
    /// and "Component Parameter" (slider) sections were removed from
    /// MainWindow.axaml in favour of the <c>IComponentEditorProvider</c>
    /// editors hosted by <c>SelectedComponentPropertiesPanel</c>. These tests
    /// pin that single-source-of-truth state so the duplicate sections cannot
    /// silently return.
    /// </summary>
    public class PropertiesPanelDeduplicationTests
    {
        private static string MainWindowAxaml =>
            File.ReadAllText(Path.Combine(FindRepoRoot(), "CAP.Avalonia", "Views", "MainWindow.axaml"));

        private static string PropertiesPanelAxaml =>
            File.ReadAllText(Path.Combine(FindRepoRoot(), "CAP.Avalonia", "Views", "Panels",
                "SelectedComponentPropertiesPanel.axaml"));

        [Fact]
        public void MainWindow_HasNoHardcodedLaserConfigurationSection()
        {
            var content = MainWindowAxaml;

            content.ShouldNotContain("Laser Configuration");
            // The laser editor's bindings must only live in the provider template.
            content.ShouldNotContain("IsLightSource");
            content.ShouldNotContain("LaserConfig");
        }

        [Fact]
        public void MainWindow_HasNoHardcodedComponentParameterSliderSection()
        {
            var content = MainWindowAxaml;

            content.ShouldNotContain("Component Parameter");
            // SliderValue/SliderLabel belong to SliderEditorViewModel's template.
            // (SliderMin/SliderMax are still legitimately used by Parameter Sweep.)
            content.ShouldNotContain("SelectedComponent.SliderValue");
            content.ShouldNotContain("SelectedComponent.SliderLabel");
        }

        [Fact]
        public void MainWindow_HostsSelectedComponentPropertiesPanel()
        {
            MainWindowAxaml.ShouldContain("SelectedComponentPropertiesPanel");
        }

        [Fact]
        public void PropertiesPanel_LightSourceTemplate_KeepsFeatureParity()
        {
            var content = PropertiesPanelAxaml;

            // Parity with the removed hard-coded section: curated wavelength
            // dropdown + input-power slider.
            content.ShouldContain("WavelengthOptions");
            content.ShouldContain("LaserConfig.WavelengthNm");
            content.ShouldContain("LaserConfig.InputPower");
        }

        [Fact]
        public void PropertiesPanel_SliderTemplate_KeepsFeatureParity()
        {
            var content = PropertiesPanelAxaml;

            content.ShouldContain("SliderLabel");
            // Two-way value binding so slider edits reach the S-matrix.
            content.ShouldContain("{Binding Value, Mode=TwoWay}");
        }

        [Fact]
        public void PropertiesPanel_SliderNumericInput_HasNoSpinnerButtons()
        {
            // Issue #779: in the 80px editor column the Fluent spinner buttons
            // covered the numeric value (e.g. directional-coupler coupling %).
            // The slider provides stepping, so the NumericUpDown must stay a
            // plain validated text input without spinner buttons.
            PropertiesPanelAxaml.ShouldContain("ShowButtonSpinner=\"False\"");
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CAP.Avalonia", "App.axaml.cs")))
                dir = dir.Parent;

            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root");
        }
    }
}
