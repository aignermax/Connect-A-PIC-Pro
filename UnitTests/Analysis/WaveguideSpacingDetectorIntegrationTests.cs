using CAP_Core.Analysis;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

public class WaveguideSpacingDetectorIntegrationTests
{
    private const double MinSpacing = 2.0;

    [Fact]
    public void DesignValidator_WithMinSpacing_SurfacesViolation()
    {
        var conn1 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var conn2 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 2.0, 100, 2.0);
        var validator = new DesignValidator();

        var issues = validator.Validate(
            new[] { conn1, conn2 },
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        issues.ShouldContain(i => i.Type == DesignIssueType.WaveguideSpacingViolation);
    }

    [Fact]
    public void DesignValidator_WithoutMinSpacing_DoesNotRunSpacingCheck()
    {
        var conn1 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var conn2 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 2.0, 100, 2.0);
        var validator = new DesignValidator();

        var issues = validator.Validate(
            new[] { conn1, conn2 },
            Array.Empty<ComponentGroup>());

        issues.ShouldNotContain(i => i.Type == DesignIssueType.WaveguideSpacingViolation);
    }
}
