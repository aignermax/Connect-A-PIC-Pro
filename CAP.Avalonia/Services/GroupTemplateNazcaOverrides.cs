using System;
using System.Collections.Generic;
using System.Text.Json;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services;

/// <summary>
/// Bridges per-instance <see cref="NazcaCodeOverride"/> entries and group templates
/// (issue #720). Overrides are design-scoped (keyed by component identifier in the
/// live override store), while the group library is design-independent — so overrides
/// travel with a template as opaque JSON and are re-keyed onto the freshly generated
/// identifiers when the template is instantiated.
/// </summary>
public static class GroupTemplateNazcaOverrides
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>Serializes an override to the JSON form stored inside a group template.</summary>
    /// <param name="nazcaOverride">The override to serialize.</param>
    /// <returns>The serialized override JSON.</returns>
    public static string Serialize(NazcaCodeOverride nazcaOverride) =>
        JsonSerializer.Serialize(nazcaOverride, JsonOptions);

    /// <summary>Deserializes an override from template JSON; null when the JSON is invalid.</summary>
    /// <param name="json">The serialized override JSON.</param>
    /// <returns>The deserialized override, or null for malformed input.</returns>
    public static NazcaCodeOverride? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NazcaCodeOverride>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates the provider handed to <c>GroupLibraryManager.SaveTemplate</c>: looks up a
    /// child identifier in the live override store and returns its serialized override.
    /// </summary>
    /// <param name="overrideStore">The design's live override store (may be null).</param>
    /// <returns>Identifier → override JSON lookup, or null when no store is available.</returns>
    public static Func<string, string?>? CreateJsonProvider(
        IDictionary<string, NazcaCodeOverride>? overrideStore)
    {
        if (overrideStore == null)
            return null;

        return identifier =>
            overrideStore.TryGetValue(identifier, out var nazcaOverride)
                ? Serialize(nazcaOverride)
                : null;
    }

    /// <summary>
    /// Builds the overrides to seed into a target design's override store after a template
    /// was instantiated: template-child overrides re-keyed onto the instance's identifiers.
    /// </summary>
    /// <param name="template">The template that was instantiated.</param>
    /// <param name="instance">The freshly deep-copied group instance.</param>
    /// <returns>Instance-child identifier → independent override copy.</returns>
    public static Dictionary<string, NazcaCodeOverride> BuildSeedMap(
        GroupTemplate template,
        ComponentGroup instance)
    {
        var seeds = new Dictionary<string, NazcaCodeOverride>();
        if (template.TemplateGroup == null || template.NazcaOverridesJson.Count == 0)
            return seeds;

        var identifierMap = GroupTemplateOverrides.BuildIdentifierMap(
            template.TemplateGroup, instance);

        foreach (var (templateId, instanceId) in identifierMap)
        {
            if (!template.NazcaOverridesJson.TryGetValue(templateId, out var json))
                continue;

            var nazcaOverride = Deserialize(json);
            if (nazcaOverride != null)
                seeds[instanceId] = nazcaOverride;
        }

        return seeds;
    }
}
