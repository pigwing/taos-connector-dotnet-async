using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    internal static class WsJson
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        internal static T Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }

        internal static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            options.Converters.Add(new ByteArrayConverter());
            return options;
        }

        private sealed class ByteArrayConverter : JsonConverter<byte[]>
        {
            public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert,
                JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    return reader.GetBytesFromBase64();
                }

                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw new JsonException("Expected a base64 string or an array of byte values.");
                }

                var values = new List<byte>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetByte(out var value))
                    {
                        throw new JsonException("Byte array values must be integers from 0 through 255.");
                    }

                    values.Add(value);
                }

                if (reader.TokenType != JsonTokenType.EndArray)
                {
                    throw new JsonException("The byte array was not terminated.");
                }

                return values.ToArray();
            }

            public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
            {
                writer.WriteBase64StringValue(value);
            }
        }
    }
}
