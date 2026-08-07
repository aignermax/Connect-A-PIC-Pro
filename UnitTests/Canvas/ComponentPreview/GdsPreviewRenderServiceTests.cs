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

    [Fact]
    public void BuildCacheKey_RotatedComponent_MatchesUnrotatedKey()
    {
        // Rotating with R swaps Component.Width/HeightMicrometers and bumps RotationDegrees.
        // The preview bitmap content is rotation-independent (the canvas rotates it at draw
        // time), so the cache key must not change — otherwise every rotation re-runs the
        // Python render and rasterises the unrotated geometry into a swapped-aspect bitmap.
        var unrotated = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.io", widthMicrometers: 8, heightMicrometers: 4);

        var rotated = TestComponentFactory.CreateComponentViewModel(
            nazcaFunctionName: "demo.io", widthMicrometers: 4, heightMicrometers: 8);
        rotated.Component.RotationDegrees = 90;

        GdsPreviewRenderService.BuildCacheKey(rotated)
            .ShouldBe(GdsPreviewRenderService.BuildCacheKey(unrotated));
    }

    [Fact]
    public void BuildCacheKey_GdsFactoryNativeComponent_ReturnsGdsfactoryKey()
    {
        // A gdsfactory-native component (no Nazca function, a gdsfactory factory) must still get
        // a cache key so it renders a real preview instead of falling back to a rectangle (#570).
        var comp = TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: "");
        comp.Component.GdsFactoryFunction = "cspdk.sin300.mmi1x2";

        var key = GdsPreviewRenderService.BuildCacheKey(comp);

        key.ShouldNotBeNull();
        key!.ShouldStartWith("gdsfactory|cspdk.sin300.mmi1x2|");
    }

    [Fact]
    public void BuildCacheKey_GdsFactoryNativeComponent_WithSynthesizedNazcaName_StillReturnsGdsfactoryKey()
    {
        // On placement, a gdsfactory-native component is given a synthesized nazcaFunction
        // ("nazca_<name>") that no Nazca script can render. The gdsfactory factory must take
        // precedence so the placed component previews via gdsfactory, not a dead Nazca call —
        // otherwise the canvas grid stays blank.
        var comp = TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: "nazca_mmi1x2");
        comp.Component.GdsFactoryFunction = "cspdk.sin300.mmi1x2";

        var key = GdsPreviewRenderService.BuildCacheKey(comp);

        key.ShouldNotBeNull();
        key!.ShouldStartWith("gdsfactory|cspdk.sin300.mmi1x2|");
    }

    [Fact]
    public async Task GetGeometry_GdsFactoryKey_RendersViaGdsFactoryServiceNotNazca()
    {
        // A gdsfactory-native render identity must be resolved by the gdsfactory preview
        // back-end (RenderRawCodeAsync with generated get_component code), never Nazca (#570).
        var nazca = new Mock<NazcaComponentPreviewService>("python", "nazca.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        // The gdsfactory back-end is typed as the base service (mockable, sealed derived type isn't).
        var gf = new Mock<NazcaComponentPreviewService>("python", "gf.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        gf.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok());

        var diskDir = Path.Combine(Path.GetTempPath(), "lunima-gf-" + Guid.NewGuid().ToString("N"));
        var svc = new GdsPreviewRenderService(nazca.Object, new GdsPreviewDiskCache(diskDir), gf.Object);
        var key = new GdsPreviewKey(null, null, null) { GdsFactoryFunction = "cspdk.sin300.mmi1x2" };

        key.IsRenderable.ShouldBeTrue();
        svc.TryGetGeometry(key).ShouldBeNull();       // miss → async render kicked off
        await svc.WaitForPendingAsync();
        svc.TryGetGeometry(key).ShouldNotBeNull();    // rendered via gdsfactory service

        gf.Verify(s => s.RenderRawCodeAsync(
            It.Is<string>(c => c.Contains("gf.get_component('mmi1x2')")), It.IsAny<CancellationToken>()), Times.Once);
        nazca.Verify(s => s.RenderAsync(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        try { Directory.Delete(diskDir, true); } catch { }
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

    // ── TryGetPreview — failure caching + render throttle ──────────────────

    [Fact]
    public async Task TryGetPreview_FailingRender_IsFetchedOnlyOncePerSession()
    {
        // A synthesized import function name ("nazca_<cell>") fails every render;
        // the failure must be remembered so the Python subprocess is spawned at
        // most once per key, not once per frame.
        var mock = new Mock<NazcaComponentPreviewService>("py", "s.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        mock.Setup(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NazcaPreviewResult.Fail("unknown function"));
        var svc = new GdsPreviewRenderService(mock.Object);
        var comp = TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: "nazca_imported_cell");

        svc.TryGetPreview(comp).ShouldBeNull();
        await svc.WaitForPendingAsync();
        svc.TryGetPreview(comp).ShouldBeNull();
        svc.TryGetPreview(comp).ShouldBeNull();
        await svc.WaitForPendingAsync();

        mock.Verify(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryGetPreview_FailureMarker_SurvivesLruEviction()
    {
        // A large import carries more unique failing keys than the LRU preview cache
        // holds; the failure markers live outside the LRU so an evicted key must not
        // re-spawn a render.
        var mock = new Mock<NazcaComponentPreviewService>("py", "s.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        mock.Setup(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NazcaPreviewResult.Fail("unknown function"));
        var svc = new GdsPreviewRenderService(mock.Object);
        var first = TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: "nazca_cell_first");

        svc.TryGetPreview(first);
        await svc.WaitForPendingAsync();
        for (int i = 0; i < GdsPreviewCache.MaxEntries + 10; i++)
            svc.TryGetPreview(TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: $"nazca_cell_{i}"));
        await svc.WaitForPendingAsync();

        svc.TryGetPreview(first).ShouldBeNull();
        await svc.WaitForPendingAsync();
        mock.Verify(s => s.RenderAsync(It.IsAny<string?>(), "nazca_cell_first", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryGetPreview_ManyUniqueKeys_RendersAtMostThreeConcurrently()
    {
        var tracker = new object();
        int active = 0, maxActive = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mock = new Mock<NazcaComponentPreviewService>("py", "s.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        mock.Setup(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string? _, string _, string? _, CancellationToken _) =>
            {
                lock (tracker) { active++; maxActive = Math.Max(maxActive, active); }
                await release.Task;
                lock (tracker) { active--; }
                return NazcaPreviewResult.Fail("blocked");
            });
        var svc = new GdsPreviewRenderService(mock.Object);

        for (int i = 0; i < 10; i++)
            svc.TryGetPreview(TestComponentFactory.CreateComponentViewModel(nazcaFunctionName: $"nazca_gate_{i}"));
        release.SetResult();
        await svc.WaitForPendingAsync();

        maxActive.ShouldBeLessThanOrEqualTo(3);
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
    public async Task GetGeometry_RenderFails_IsNotPersisted_AndRetriesOnNextInstance()
    {
        // A failed render (broken/half-provisioned interpreter) must NOT be persisted as an
        // empty marker — otherwise the component stays blank forever, even after the env is
        // fixed. A fresh instance must re-attempt the render.
        var diskDir = Path.Combine(Path.GetTempPath(), "lunima-fail-" + Guid.NewGuid().ToString("N"));
        var key = new GdsPreviewKey("m", "f", "p");

        var failing = new Mock<NazcaComponentPreviewService>("py", "s.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        failing.Setup(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NazcaPreviewResult.Fail("interpreter not ready"));
        var svc1 = new GdsPreviewRenderService(failing.Object, new GdsPreviewDiskCache(diskDir));
        svc1.TryGetGeometry(key);
        await svc1.WaitForPendingAsync();

        // A new instance (e.g. after the env is fixed) must render, not serve a persisted "empty".
        var ok = new Mock<NazcaComponentPreviewService>("py", "s.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        ok.Setup(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok());
        var svc2 = new GdsPreviewRenderService(ok.Object, new GdsPreviewDiskCache(diskDir));
        svc2.TryGetGeometry(key);
        await svc2.WaitForPendingAsync();

        svc2.TryGetGeometry(key).ShouldNotBeNull();
        ok.Verify(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        try { Directory.Delete(diskDir, true); } catch { }
    }

    [Fact]
    public async Task GetGeometry_GenuinelyEmptyRender_PersistsEmpty_NoRetry()
    {
        // A successful render with 0 polygons is genuinely empty (not a failure) — persist it so
        // a second instance does not pointlessly re-render nothing.
        var diskDir = Path.Combine(Path.GetTempPath(), "lunima-empty-" + Guid.NewGuid().ToString("N"));
        var key = new GdsPreviewKey("m", "f", "p");

        var empty = new Mock<NazcaComponentPreviewService>("py", "s.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        empty.Setup(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NazcaPreviewResult { Success = true, Polygons = new List<NazcaPreviewPolygon>() });
        var svc1 = new GdsPreviewRenderService(empty.Object, new GdsPreviewDiskCache(diskDir));
        svc1.TryGetGeometry(key);
        await svc1.WaitForPendingAsync();

        var mock2 = new Mock<NazcaComponentPreviewService>("py", "s.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        var svc2 = new GdsPreviewRenderService(mock2.Object, new GdsPreviewDiskCache(diskDir));
        svc2.TryGetGeometry(key);
        await svc2.WaitForPendingAsync();
        mock2.Verify(s => s.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

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
