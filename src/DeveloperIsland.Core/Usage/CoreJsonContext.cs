using System.Text.Json.Serialization;
using DeveloperIsland.Core.Pricing;

namespace DeveloperIsland.Core.Usage;

/// <summary>Source-generated JSON metadata, so Core works with reflection-based serialization disabled.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodexFileState))]
[JsonSerializable(typeof(PricingOverrides))]
[JsonSerializable(typeof(DeveloperIsland.Core.Focus.FocusSessionState))]
[JsonSerializable(typeof(List<DeveloperIsland.Core.Tasks.TaskItem>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(DeveloperIsland.Core.Settings.UiState))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
