using System.Collections.Concurrent;
using Avalonia.Threading;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Export;

namespace CAP.Avalonia.Controls.Canvas.ComponentPreview;

/// <summary>
/// Manages async fetching and caching of GDS preview thumbnails for canvas components.
/// </summary>
/// <remarks>
/// <para>
/// The first call to <see cref="TryGetPreview"/> for a given template triggers a
/// background fetch via <see cref="NazcaComponentPreviewService"/>.  While the fetch
/// is in progress the method returns <c>null</c> so the caller can fall back to the
/// legacy rectangle renderer.  Once the result arrives <see cref="OnPreviewLoaded"/>
/// is fired on the UI thread so the canvas can call <c>InvalidateVisual()</c>.
/// </para>
/// <para>
/// Failures (Python unavailable, script timeout, 0 polygons) are cached as <c>null</c>
/// so no further retries are attempted during the session — the component simply stays
/// as a legacy rectangle.
/// </para>
/// </remarks>
public sealed class GdsPreviewRenderService
{
    /// <summary>Lower bound on bitmap dimensions to avoid zero-size bitmaps.</summary>
    internal const int MinBitmapPixels = 16;

    private readonly NazcaComponentPreviewService _previewService;

    /// <summary>Renders gdsfactory-native components (cspdk etc.); null falls back to no preview.
    /// Typed as the base so it can be mocked in tests; DI injects the
    /// <see cref="GdsFactoryComponentPreviewService"/> instance.</summary>
    private readonly NazcaComponentPreviewService? _gdsFactoryPreviewService;

    private readonly GdsPreviewCache _cache = new();

    /// <summary>Persistent on-disk cache for resolution-independent geometry.</summary>
    private readonly GdsPreviewDiskCache _diskCache;

    /// <summary>Throttles concurrent Python renders so the library can't spawn a flood.</summary>
    private readonly SemaphoreSlim _renderGate = new(3, 3);

    /// <summary>In-memory LRU of geometry keyed by <see cref="GdsPreviewKey.Hash"/>.</summary>
    private readonly GdsGeometryCache _memGeometry = new();

    /// <summary>Tracks in-flight geometry fetches keyed by render-identity hash.</summary>
    private readonly ConcurrentDictionary<string, Task> _pending = new();

    /// <summary>Tracks keys for which a fetch is currently in flight.</summary>
    private readonly ConcurrentDictionary<string, byte> _pendingFetches = new();

    /// <summary>
    /// Raised on the UI thread whenever a previously-pending preview finishes
    /// loading.  Subscribe with <c>+= canvas.InvalidateVisual</c> from
    /// <see cref="CAP.Avalonia.Controls.DesignCanvas"/> (and from thumbnails) to
    /// trigger a repaint.
    /// </summary>
    public event Action? OnPreviewLoaded;

    /// <summary>
    /// Initializes the service with the shared Nazca preview back-end and a
    /// default disk cache.
    /// </summary>
    public GdsPreviewRenderService(
        NazcaComponentPreviewService previewService,
        NazcaComponentPreviewService? gdsFactoryPreviewService = null)
        : this(previewService, new GdsPreviewDiskCache(), gdsFactoryPreviewService)
    {
    }

    /// <summary>
    /// Initializes the service with the shared Nazca preview back-end, an explicit disk cache
    /// (used by tests to redirect cache files), and an optional gdsfactory preview back-end
    /// for gdsfactory-native components (#570).
    /// </summary>
    public GdsPreviewRenderService(
        NazcaComponentPreviewService previewService, GdsPreviewDiskCache diskCache,
        NazcaComponentPreviewService? gdsFactoryPreviewService = null)
    {
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _diskCache = diskCache ?? throw new ArgumentNullException(nameof(diskCache));
        _gdsFactoryPreviewService = gdsFactoryPreviewService;
    }

    /// <summary>
    /// Returns cached <see cref="GdsPreviewData"/> for the given component template,
    /// or <c>null</c> while a background fetch is pending or when no preview is
    /// available (unknown Nazca function, Python unavailable, empty polygon list).
    /// </summary>
    /// <param name="comp">The component for which to fetch/retrieve the preview.</param>
    public GdsPreviewData? TryGetPreview(ComponentViewModel comp)
    {
        var cacheKey = BuildCacheKey(comp);
        if (cacheKey == null)
            return null;

        if (_cache.TryGet(cacheKey, out var cached))
            return cached;

        // Enqueue a background fetch only once per key
        if (_pendingFetches.TryAdd(cacheKey, 0))
            _ = FetchAndCacheAsync(cacheKey, comp);

        return null;
    }

    /// <summary>
    /// Builds the cache key for a component.
    /// Returns <c>null</c> when no Nazca function name is available (built-in or
    /// external-port components).
    /// </summary>
    internal static string? BuildCacheKey(ComponentViewModel comp)
    {
        // Key on the UNROTATED dimensions: the cached bitmap holds unrotated geometry, so
        // keying on the live (rotation-swapped) dims would re-run the Python render on every
        // rotation and rasterise with a distorted aspect ratio.
        var (width, height) = GetUnrotatedDimensions(comp);

        // gdsfactory-native components take precedence over the Nazca function: placement gives
        // them a synthesized nazcaFunction ("nazca_<name>") no Nazca script can render, so the
        // module-qualified GdsFactoryFunction is the real render identity.
        if (IsGdsFactoryNative(comp.Component))
            return $"gdsfactory|{comp.Component.GdsFactoryFunction}|{width:F2}|{height:F2}";

        var fn = comp.Component.NazcaFunctionName;
        if (!string.IsNullOrWhiteSpace(fn))
            return $"{fn}|{width:F2}|{height:F2}";

        return null;
    }

    private static (double Width, double Height) GetUnrotatedDimensions(ComponentViewModel comp) =>
        GdsPolygonRenderer.GetUnrotatedSize(comp.Component.RotationDegrees, comp.Width, comp.Height);

    /// <summary>
    /// True when the component is gdsfactory-native: it carries a module-qualified
    /// <see cref="Component.GdsFactoryFunction"/> (e.g. "cspdk.sin300.mmi1x2"). Such components
    /// render via the gdsfactory back-end, never Nazca — even if they also carry a synthesized
    /// nazcaFunction fallback from placement.
    /// </summary>
    private static bool IsGdsFactoryNative(CAP_Core.Components.Core.Component comp) =>
        !string.IsNullOrWhiteSpace(comp.GdsFactoryFunction) && comp.GdsFactoryFunction!.Contains('.');

    /// <summary>
    /// Renders a gdsfactory-native component's geometry via the gdsfactory preview back-end,
    /// or a failure result when no service is wired / the function is not module-qualified (#570).
    /// </summary>
    private async Task<NazcaPreviewResult> RenderGdsFactoryAsync(string? gdsFactoryFunction)
    {
        var code = GdsFactoryPreviewCode.For(gdsFactoryFunction);
        if (code == null || _gdsFactoryPreviewService == null)
            return NazcaPreviewResult.Fail("No gdsfactory preview available for this component.");
        return await _gdsFactoryPreviewService.RenderRawCodeAsync(code);
    }

    private async Task FetchAndCacheAsync(string cacheKey, ComponentViewModel comp)
    {
        NazcaPreviewResult result;
        try
        {
            if (IsGdsFactoryNative(comp.Component))
            {
                // Precedence over the (possibly synthesized) nazcaFunction — see BuildCacheKey.
                result = await RenderGdsFactoryAsync(comp.Component.GdsFactoryFunction);
            }
            else
            {
                var module = comp.Component.NazcaModuleName;
                var function = comp.Component.NazcaFunctionName;
                var parameters = comp.Component.NazcaFunctionParameters;
                result = await _previewService.RenderAsync(module, function, parameters);
            }
        }
        catch
        {
            result = NazcaPreviewResult.Fail("Unexpected error during GDS preview fetch.");
        }

        // Rasterise in the unrotated frame — the canvas applies the rotation at draw time.
        var (unrotatedW, unrotatedH) = GetUnrotatedDimensions(comp);
        var data = result.Success && result.Polygons.Count > 0
            ? new GdsPreviewData(result, unrotatedW, unrotatedH)
            : null;

        // Cache before removing the pending-fetch marker so a concurrent caller
        // that arrives between these two lines will find the cached entry rather
        // than enqueue a duplicate fetch.
        _cache.Set(cacheKey, data);
        _pendingFetches.TryRemove(cacheKey, out _);

        if (data != null)
        {
            int bitmapW = Math.Max(GdsPreviewRenderService.MinBitmapPixels, (int)Math.Ceiling(unrotatedW));
            int bitmapH = Math.Max(GdsPreviewRenderService.MinBitmapPixels, (int)Math.Ceiling(unrotatedH));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var bitmap = GdsPolygonRenderer.RasterizeToBitmap(data.Result, bitmapW, bitmapH);
                _cache.Set(cacheKey, data with { Bitmap = bitmap });
                OnPreviewLoaded?.Invoke();
            });
        }
    }

    /// <summary>
    /// Returns the cached preview geometry for a render identity, or null while a
    /// background fetch is pending / when no geometry is available. Lookup chain:
    /// in-memory LRU -> disk cache -> Python render (throttled).
    /// </summary>
    public NazcaPreviewResult? TryGetGeometry(GdsPreviewKey key)
    {
        if (!key.IsRenderable) return null;
        var cacheKey = key.Hash();
        if (_memGeometry.TryGet(cacheKey, out var cached)) return cached;
        // Reserve the slot BEFORE starting the fetch (mirrors the canvas TryGetPreview
        // path) so a duplicate fetch is never launched for the same key. Passing the
        // started task straight into TryAdd would run the task before TryAdd decides
        // to keep it, defeating the _pending dedup under concurrent callers.
        if (_pending.TryAdd(cacheKey, Task.CompletedTask))
            _pending[cacheKey] = FetchGeometryAsync(key, cacheKey);
        return null;
    }

    /// <summary>Test hook: awaits all in-flight geometry fetches.</summary>
    public Task WaitForPendingAsync() => Task.WhenAll(_pending.Values.ToArray());

    private async Task FetchGeometryAsync(GdsPreviewKey key, string cacheKey)
    {
        try
        {
            if (_diskCache.TryRead(key, out var disk))
            {
                _memGeometry.Set(cacheKey, disk);
                RaisePreviewLoaded();
                return;
            }
            await _renderGate.WaitAsync();
            NazcaPreviewResult result;
            try
            {
                result = string.IsNullOrWhiteSpace(key.Function)
                    ? await RenderGdsFactoryAsync(key.GdsFactoryFunction)
                    : await _previewService.RenderAsync(key.Module, key.Function!, key.Parameters);
            }
            finally { _renderGate.Release(); }

            if (result.Success && result.Polygons.Count > 0)
            {
                _diskCache.Write(key, result);
                _memGeometry.Set(cacheKey, result);
            }
            else if (result.Success)
            {
                // A genuinely empty render (0 polygons) — persist the empty marker so we don't
                // keep re-rendering a component that has no geometry.
                _diskCache.WriteEmpty(key);
                _memGeometry.Set(cacheKey, null);
            }
            else
            {
                // The render FAILED (Python/env/script error — e.g. cspdk not yet installed, a
                // broken or half-provisioned interpreter). Do NOT persist: a transient env failure
                // must not poison the disk cache permanently, or the component stays blank forever
                // even after the env is fixed. Remember null for this session only (like the catch
                // block below), so the next launch retries. (#570 field test.)
                _memGeometry.Set(cacheKey, null);
            }
            RaisePreviewLoaded();
        }
        catch
        {
            // Transient failure (e.g. Python hiccup): remember "empty" for this session
            // only — deliberately NOT WriteEmpty, so a restart can retry. A genuinely
            // empty render (above) persists the empty marker; a crash does not.
            _memGeometry.Set(cacheKey, null);
        }
        finally
        {
            _pending.TryRemove(cacheKey, out _);
        }
    }

    /// <summary>
    /// Raises <see cref="OnPreviewLoaded"/> on the UI thread. Safe in headless tests:
    /// when there are no subscribers the dispatcher is never touched.
    /// </summary>
    private void RaisePreviewLoaded()
    {
        var handler = OnPreviewLoaded;
        if (handler == null) return;
        try { Dispatcher.UIThread.Post(() => handler()); }
        catch { /* no dispatcher in headless tests */ }
    }
}
