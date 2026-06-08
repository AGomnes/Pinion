using Xunit;

namespace Pinion.Tests;

/// <summary>
/// Serializes the end-to-end tests that drive `dotnet test` against the shared LegacyShop.Tests
/// project (and its single PinionCharacterization output dir). Without this they run in parallel and
/// clobber each other's generated files / build outputs.
/// </summary>
[CollectionDefinition("LegacyShop e2e", DisableParallelization = true)]
public sealed class LegacyShopE2ECollection { }
