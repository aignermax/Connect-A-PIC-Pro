using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PinKinds
{
    /// <summary>
    /// Verifies that the bundled PDKs declare electrical pins on active components
    /// (Issue #680, follow-up to #519/#623): Phase Shifter and Photodetector in the
    /// Demo PDK carry heater/diode contacts, the SiEPIC Bond Pad is purely electrical,
    /// and all passive components stay purely optical.
    /// </summary>
    public class BundledPdkElectricalPinsTests
    {
        private static string PdkPath(string fileName) => Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..",
            "CAP-DataAccess", "PDKs", fileName);

        private static PdkDraft LoadPdk(string fileName)
            => new PdkLoader().LoadFromFile(PdkPath(fileName));

        private static MatterType KindOf(PdkComponentDraft comp, string pinName)
            => PinKindHelper.Parse(comp.Pins.Single(p => p.Name == pinName).PinKind);

        [Fact]
        public void DemoPdk_PhaseShifter_HasTwoElectricalHeaterContacts()
        {
            var pdk = LoadPdk("demo-pdk.json");
            var phaseShifter = pdk.Components.Single(c => c.Name == "Phase Shifter");

            KindOf(phaseShifter, "elec1").ShouldBe(MatterType.Electricity);
            KindOf(phaseShifter, "elec2").ShouldBe(MatterType.Electricity);
            KindOf(phaseShifter, "in").ShouldBe(MatterType.Light);
            KindOf(phaseShifter, "out").ShouldBe(MatterType.Light);
        }

        [Fact]
        public void DemoPdk_Photodetector_HasAnodeAndCathode()
        {
            var pdk = LoadPdk("demo-pdk.json");
            var photodetector = pdk.Components.Single(c => c.Name == "Photodetector");

            KindOf(photodetector, "anode").ShouldBe(MatterType.Electricity);
            KindOf(photodetector, "cathode").ShouldBe(MatterType.Electricity);
            KindOf(photodetector, "in").ShouldBe(MatterType.Light);
        }

        [Fact]
        public void SiepicPdk_BondPad_IsPurelyElectrical()
        {
            var pdk = LoadPdk("siepic-ebeam-pdk.json");
            var bondPad = pdk.Components.Single(c => c.Name == "Bond Pad");

            bondPad.Pins.ShouldNotBeEmpty();
            foreach (var pin in bondPad.Pins)
            {
                PinKindHelper.Parse(pin.PinKind).ShouldBe(MatterType.Electricity,
                    $"Bond Pad pin '{pin.Name}' must be electrical");
            }
        }

        /// <summary>
        /// Passive optical devices have no metal contacts, so no bundled component
        /// outside the known active set may declare an electrical pin.
        /// </summary>
        [Theory]
        [InlineData("demo-pdk.json", "Phase Shifter", "Photodetector", "Probe Pad")]
        [InlineData("siepic-ebeam-pdk.json", "Bond Pad")]
        [InlineData("cornerstone-sin-pdk.json")]
        [InlineData("tools-pdk.json")]
        public void BundledPdks_PassiveComponents_HaveNoElectricalPins(
            string pdkFile, params string[] activeComponentNames)
        {
            var pdk = LoadPdk(pdkFile);

            var passiveWithElectricalPins = pdk.Components
                .Where(c => !activeComponentNames.Contains(c.Name))
                .Where(c => c.Pins.Any(p => PinKindHelper.Parse(p.PinKind) == MatterType.Electricity))
                .Select(c => c.Name)
                .ToList();

            passiveWithElectricalPins.ShouldBeEmpty(
                $"Passive components in {pdkFile} must stay purely optical");
        }
    }
}
