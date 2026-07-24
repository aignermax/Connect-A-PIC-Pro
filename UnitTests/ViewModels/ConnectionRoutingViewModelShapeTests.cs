using System.Reflection;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Guards that the per-connection routing panel VM stays reduced to the style dropdown plus
/// selection: the discarded width / bend-radius / freeze / per-bend number UI must not return.
/// </summary>
public class ConnectionRoutingViewModelShapeTests
{
    private static readonly BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    [Theory]
    [InlineData("RoutingStyles")]
    [InlineData("SelectedStyle")]
    [InlineData("SelectedConnection")]
    public void KeepsEssentialMembers(string member)
    {
        typeof(ConnectionRoutingViewModel).GetMember(member, PublicInstance)
            .ShouldNotBeEmpty($"'{member}' must remain on the slimmed routing VM.");
    }

    [Theory]
    [InlineData("WidthMicrometers")]
    [InlineData("BendRadiusMicrometers")]
    [InlineData("IsRouteFrozen")]
    [InlineData("BendCount")]
    [InlineData("BendNumber")]
    [InlineData("BendOverrideRadiusMicrometers")]
    [InlineData("StatusText")]
    [InlineData("ApplyBendRadiusCommand")]
    public void DropsNumberPanelMembers(string member)
    {
        typeof(ConnectionRoutingViewModel).GetMember(member, PublicInstance)
            .ShouldBeEmpty($"'{member}' is part of the discarded number panel and must be gone.");
    }
}
