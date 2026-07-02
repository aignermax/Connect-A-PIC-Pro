using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PinKinds
{
    public class PinKindHelperTests
    {
        [Theory]
        [InlineData(null, MatterType.Light)]
        [InlineData("", MatterType.Light)]
        [InlineData("   ", MatterType.Light)]
        [InlineData("Optical", MatterType.Light)]
        [InlineData("optical", MatterType.Light)]
        [InlineData("OPTICAL", MatterType.Light)]
        [InlineData("Electrical", MatterType.Electricity)]
        [InlineData("electrical", MatterType.Electricity)]
        public void TryParse_ValidValues_ReturnsExpectedMatterType(string? pinKind, MatterType expected)
        {
            PinKindHelper.TryParse(pinKind, out var matterType).ShouldBeTrue();
            matterType.ShouldBe(expected);
        }

        [Theory]
        [InlineData("Metal")]
        [InlineData("Light")]
        [InlineData("DC")]
        [InlineData("elec")]
        public void TryParse_InvalidValues_ReturnsFalse(string pinKind)
        {
            PinKindHelper.TryParse(pinKind, out _).ShouldBeFalse();
        }

        [Fact]
        public void Parse_InvalidValue_Throws()
        {
            Should.Throw<ArgumentException>(() => PinKindHelper.Parse("Metal"));
        }

        [Fact]
        public void Parse_NullDefaultsToOptical()
        {
            PinKindHelper.Parse(null).ShouldBe(MatterType.Light);
        }

        [Theory]
        [InlineData(MatterType.Light, "Optical")]
        [InlineData(MatterType.Electricity, "Electrical")]
        public void ToKindName_MapsMatterTypeToUserFacingName(MatterType matterType, string expected)
        {
            PinKindHelper.ToKindName(matterType).ShouldBe(expected);
        }

        [Fact]
        public void AreKindsCompatible_SameKind_ReturnsTrue()
        {
            var optical1 = CreatePhysicalPin(MatterType.Light);
            var optical2 = CreatePhysicalPin(MatterType.Light);
            var electrical1 = CreatePhysicalPin(MatterType.Electricity);
            var electrical2 = CreatePhysicalPin(MatterType.Electricity);

            PinKindHelper.AreKindsCompatible(optical1, optical2).ShouldBeTrue();
            PinKindHelper.AreKindsCompatible(electrical1, electrical2).ShouldBeTrue();
        }

        [Fact]
        public void AreKindsCompatible_CrossKind_ReturnsFalse()
        {
            var optical = CreatePhysicalPin(MatterType.Light);
            var electrical = CreatePhysicalPin(MatterType.Electricity);

            PinKindHelper.AreKindsCompatible(optical, electrical).ShouldBeFalse();
            PinKindHelper.AreKindsCompatible(electrical, optical).ShouldBeFalse();
        }

        [Fact]
        public void PhysicalPin_WithoutLogicalPin_DefaultsToOptical()
        {
            var pin = new PhysicalPin { Name = "a0" };
            pin.MatterType.ShouldBe(MatterType.Light);
        }

        [Fact]
        public void PhysicalPin_DerivesKindFromLogicalPin()
        {
            var pin = CreatePhysicalPin(MatterType.Electricity);
            pin.MatterType.ShouldBe(MatterType.Electricity);
        }

        [Fact]
        public void Pin_Clone_PreservesElectricalKind()
        {
            var pin = new Pin("m_pin_top", 0, MatterType.Electricity, RectSide.Up);
            var clone = (Pin)pin.Clone();
            clone.MatterType.ShouldBe(MatterType.Electricity);
        }

        private static PhysicalPin CreatePhysicalPin(MatterType matterType) => new()
        {
            Name = "p0",
            LogicalPin = new Pin("p0", 0, matterType, RectSide.Right)
        };
    }
}
