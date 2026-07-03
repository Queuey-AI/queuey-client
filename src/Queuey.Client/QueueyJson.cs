using System.Text.Json;
using System.Text.Json.Serialization;

namespace Queuey.Client;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for all Queuey wire (de)serialization.
/// The Queuey API serializes every DTO in camelCase; these options match it and omit
/// <c>null</c> optional fields on write.
/// </summary>
/// <remarks>
/// No global string-enum converter is registered on purpose: the API's management endpoints
/// serialize enums as <b>integers</b> (framework default), while the WaaS/ingress surfaces emit
/// enum <b>names</b> as strings. Enum-valued fields are therefore modeled per-field as the exact
/// wire type (string or int-backed enum), not via a blanket converter.
/// </remarks>
public static class QueueyJson
{
    /// <summary>The canonical options used by the SDK for request/response bodies.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        // Web defaults => camelCase property names + case-insensitive reads + numbers-from-strings.
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
