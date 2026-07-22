using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Serializes tests that mutate the process-wide <c>LocalizationService.Instance</c> language.
/// The instance is a global singleton every <c>{loc:Localize}</c> binding — and now the
/// active-process badge / PDK source badges — reads from, so a test that live-switches the
/// language must not run concurrently with any other test, or those readers would observe a
/// transient foreign language. <see cref="CollectionDefinitionAttribute.DisableParallelization"/>
/// makes this collection run in isolation from every other collection.
/// </summary>
[CollectionDefinition("LocalizationSingleton", DisableParallelization = true)]
public sealed class LocalizationSingletonCollection
{
}
