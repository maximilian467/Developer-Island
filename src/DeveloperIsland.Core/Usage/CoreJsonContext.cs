using System.Text.Json.Serialization;
using DeveloperIsland.Core.Pricing;

namespace DeveloperIsland.Core.Usage;

/// <summary>Source-generated JSON metadata, so Core works with reflection-based serialization disabled.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CodexFileState))]
[JsonSerializable(typeof(PricingOverrides))]
[JsonSerializable(typeof(DeveloperIsland.Core.Focus.FocusSessionState))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
