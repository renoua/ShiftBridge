using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiftBridge.Core.Config;

/// <summary>
/// Énumérations écrites par leur nom, comme <see cref="JsonStringEnumConverter"/>, mais une valeur inconnue (config écrite par
/// une version plus récente, retour depuis une version de test) ne fait plus échouer tout le chargement : elle devient une
/// valeur hors de l'énumération, que <see cref="AppConfig.Normalize"/> retire ensuite. Sans cela, une seule action inconnue
/// remettait toute la configuration à zéro, clés SRAM AXS comprises.
/// </summary>
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;
}

public sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    /// <summary>Valeur rendue pour un nom inconnu : tous les bits à 1 (255 pour un byte, -1 pour un int), jamais définie ici.</summary>
    public static T Unknown { get; } = (T)Enum.ToObject(typeof(T), ulong.MaxValue);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : Unknown;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out long n) && Enum.IsDefined((T)Enum.ToObject(typeof(T), n))
                    ? (T)Enum.ToObject(typeof(T), n) : Unknown;
            default:
                throw new JsonException($"Valeur inattendue pour {typeof(T).Name}");
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
