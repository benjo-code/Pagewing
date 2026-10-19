using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pagewing.Core.Settings;

/// <summary>Source-generated JSON metadata for the settings file (no reflection, Native AOT safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
