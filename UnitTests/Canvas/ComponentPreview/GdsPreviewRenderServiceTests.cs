using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using Moq;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Canvas.ComponentPreview;

/// <summary>Unit tests for <see cref="GdsPreviewRenderService"/>.</summary>
public sealed class GdsPreviewRenderServiceTests
{
    // ── BuildCacheKey ───────────────────────────────────────────────────────

    [Fact]
    public void BuildCacheKey_ComponentWithNazcaFunction_ReturnsKeyWithFunctionAndDimensions()
    {
        var comp = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.mmi1x2_sh");

        var key = GdsPreviewRenderService.BuildCacheKey(comp);

        key.ShouldNotBeNull();
        key!.ShouldStartWith("demo.mmi1x2_sh|");
    }

    [Fact]
    public void BuildCacheKey_ComponentWithEmptyNazcaFunction_ReturnsNull()
    {
        var comp = TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: "");
        GdsPreviewRenderService.BuildCacheKey(comp).ShouldBeNull();
    }

    [Fact]
    public void BuildCacheKey_ComponentWithNullNazcaFunction_ReturnsNull()
    {
        var comp = TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: null);
        GdsPreviewRenderService.BuildCacheKey(comp).ShouldBeNull();
    }

    [Fact]
    public void BuildCacheKey_DifferentDimensions_ReturnsDifferentKeys()
    {
        // Components with same function but different sizes should have different keys
        var comp1 = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.io", widthMicrometers: 4, heightMicrometers: 4);
        var comp2 = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.io", widthMicrometers: 8, heightMicrometers: 4);

        var key1 = GdsPreviewRenderService.BuildCacheKey(comp1);
        var key2 = GdsPreviewRenderService.BuildCacheKey(comp2);

        key1.ShouldNotBe(key2);
    }

    // ── TryGetPreview — fallback behaviour ─────────────────────────────────

    [Fact]
    public void TryGetPreview_ComponentWithoutNazcaFunction_ReturnsNull()
    {
        var service = new GdsPreviewRenderService(
            new NazcaComponentPreviewService("python3", "/nonexistent/script.py"));

        var comp = TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: "");

        // Should return null immediately (no fetch triggered)
        service.TryGetPreview(comp).ShouldBeNull();
    }

    [Fact]
    public void TryGetPreview_FirstCallWithNazcaFunction_ReturnsNullWhileFetching()
    {
        var service = new GdsPreviewRenderService(
            new NazcaComponentPreviewService("python3", "/nonexistent/script.py"));

        var comp = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.mmi1x2_sh");

        // First call enqueues fetch and returns null (fetch not yet complete)
        var result = service.TryGetPreview(comp);
        result.ShouldBeNull();
    }

    // ── BuildCacheKey — raw-code override ──────────────────────────────────

    [Fact]
    public void BuildCacheKey_WithRawCode_ReturnsRawcodePrefixedKey()
    {
        var comp = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.mmi1x2_sh");

        var key = GdsPreviewRenderService.BuildCacheKey(comp, rawCode: "import nazca");

        key.ShouldNotBeNull();
        key!.ShouldStartWith("rawcode|");
    }

    [Fact]
    public void BuildCacheKey_SameRawCode_ReturnsSameKey()
    {
        var comp = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.mmi1x2_sh");
        const string code = "import nazca\ncell = nazca.Cell(name='test')";

        var key1 = GdsPreviewRenderService.BuildCacheKey(comp, rawCode: code);
        var key2 = GdsPreviewRenderService.BuildCacheKey(comp, rawCode: code);

        key1.ShouldBe(key2);
    }

    [Fact]
    public void BuildCacheKey_DifferentRawCode_ReturnsDifferentKeys()
    {
        var comp = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.mmi1x2_sh");

        var key1 = GdsPreviewRenderService.BuildCacheKey(comp, rawCode: "code version 1");
        var key2 = GdsPreviewRenderService.BuildCacheKey(comp, rawCode: "code version 2");

        key1.ShouldNotBe(key2);
    }

    [Fact]
    public void BuildCacheKey_RawCodeWithNoNazcaFunction_StillReturnsKey()
    {
        // A raw-code override must produce a cache key even when NazcaFunctionName is empty,
        // because the raw code completely replaces the template function.
        var comp = TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: "");

        var key = GdsPreviewRenderService.BuildCacheKey(comp, rawCode: "import nazca");

        key.ShouldNotBeNull();
        key!.ShouldStartWith("rawcode|");
    }

    [Fact]
    public void BuildCacheKey_RawCodeKeyDiffersFromTemplateKey()
    {
        // Ensure a raw-code key never collides with the template key for the same component.
        var comp = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.mmi1x2_sh");

        var templateKey = GdsPreviewRenderService.BuildCacheKey(comp, rawCode: null);
        var rawKey      = GdsPreviewRenderService.BuildCacheKey(comp, rawCode: "some code");

        rawKey.ShouldNotBe(templateKey);
    }

    // ── TryGetPreview — raw-code lookup ───────────────────────────────────

    [Fact]
    public void TryGetPreview_WithRawCodeLookup_ReturnsNullWhileFetching()
    {
        var service = new GdsPreviewRenderService(
            new NazcaComponentPreviewService("python3", "/nonexistent/script.py"))
        {
            RawCodeLookup = _ => "import nazca"
        };

        var comp = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.mmi1x2_sh");

        // Should enqueue a raw-code fetch and return null (not yet complete)
        service.TryGetPreview(comp).ShouldBeNull();
    }

    [Fact]
    public void TryGetPreview_RawCodeLookupReturnsNull_FallsBackToTemplateKey()
    {
        // When the lookup returns null the template cache key must be used, not a raw-code key.
        var service = new GdsPreviewRenderService(
            new NazcaComponentPreviewService("python3", "/nonexistent/script.py"))
        {
            RawCodeLookup = _ => null
        };

        var comp = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.shallow.strt");

        // Should behave exactly like no RawCodeLookup set — returns null while fetching
        service.TryGetPreview(comp).ShouldBeNull();
    }

    [Fact]
    public void TryGetPreview_ComponentWithoutNazcaFunctionAndNoRawCode_ReturnsNull()
    {
        var service = new GdsPreviewRenderService(
            new NazcaComponentPreviewService("python3", "/nonexistent/script.py"))
        {
            RawCodeLookup = _ => null
        };

        var comp = TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: "");

        // No function name and no raw code → no thumbnail possible
        service.TryGetPreview(comp).ShouldBeNull();
    }

    // ── TryGetGeometry — key-based lookup with disk cache + render throttle ──

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 4, YMax = 2,
        Polygons = new List<NazcaPreviewPolygon>
        {
            new() { Layer = 1, Vertices = new List<(double, double)> { (0, 0), (4, 0), (4, 2) } }
        }
    };

    [Fact]
    public async Task GetGeometry_RendersOnce_ThenServesFromMemory()
    {
        var mock = new Mock<NazcaComponentPreviewService>("python", "script.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        mock.Setup(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok());

        var diskDir = Path.Combine(Path.GetTempPath(), "lunima-svc-" + Guid.NewGuid().ToString("N"));
        var svc = new GdsPreviewRenderService(mock.Object, new GdsPreviewDiskCache(diskDir));
        var key = new GdsPreviewKey("m", "f", "p");

        svc.TryGetGeometry(key).ShouldBeNull();      // miss → async render kicked off
        await svc.WaitForPendingAsync();
        svc.TryGetGeometry(key).ShouldNotBeNull();   // now in memory
        mock.Verify(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        try { Directory.Delete(diskDir, true); } catch { }
    }

    [Fact]
    public async Task GetGeometry_SecondInstance_ServesFromDisk_NoRender()
    {
        var diskDir = Path.Combine(Path.GetTempPath(), "lunima-svc-" + Guid.NewGuid().ToString("N"));
        var key = new GdsPreviewKey("m", "f", "p");

        var mock1 = new Mock<NazcaComponentPreviewService>("python", "script.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        mock1.Setup(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok());
        var svc1 = new GdsPreviewRenderService(mock1.Object, new GdsPreviewDiskCache(diskDir));
        svc1.TryGetGeometry(key);
        await svc1.WaitForPendingAsync();   // populates disk

        var mock2 = new Mock<NazcaComponentPreviewService>("python", "script.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        var svc2 = new GdsPreviewRenderService(mock2.Object, new GdsPreviewDiskCache(diskDir));
        svc2.TryGetGeometry(key);
        await svc2.WaitForPendingAsync();
        svc2.TryGetGeometry(key).ShouldNotBeNull();
        mock2.Verify(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        try { Directory.Delete(diskDir, true); } catch { }
    }
}
