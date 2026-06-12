using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnionHopV3.Core.Security;

namespace OnionHopV3.Core.Models;

/// <summary>
/// JSON converter for a secret string: serializes it in protected (DPAPI-encrypted on Windows) form
/// and deserializes it back to plaintext in memory. Applied to settings fields that hold credentials
/// so they are not persisted to settings.json in cleartext. Legacy plaintext values are read back
/// transparently (see <see cref="SecretProtector"/>).
/// </summary>
public sealed class ProtectedStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return SecretProtector.Unprotect(reader.TokenType == JsonTokenType.Null ? null : reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        var protectedValue = SecretProtector.Protect(value);
        if (protectedValue == null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(protectedValue);
        }
    }
}
