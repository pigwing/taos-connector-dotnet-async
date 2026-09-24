using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TDengine.Driver.Impl.WebSocketMethods;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;
using Xunit;

namespace Driver.Test.Client.Query
{
    public sealed class WebSocketJsonContractOffline
    {
        private const string ProtocolNamespace = "TDengine.Driver.Impl.WebSocketMethods.Protocol";

        private static readonly Type[] ProtocolTypes = GetProtocolTypes();

        [Fact]
        public void EveryConcreteProtocolModelHasExplicitUniqueWireNames()
        {
            Assert.NotEmpty(ProtocolTypes);

            foreach (var type in ProtocolTypes)
            {
                var wireNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in GetSerializableProperties(type))
                {
                    var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
                    Assert.NotNull(attribute);
                    Assert.False(string.IsNullOrWhiteSpace(attribute!.Name),
                        $"{type.FullName}.{property.Name} has an empty wire name.");
                    Assert.True(wireNames.Add(attribute.Name),
                        $"{type.FullName} has duplicate wire name '{attribute.Name}'.");
                }
            }

            Assert.Equal(68, ProtocolTypes.Length);
        }

        [Fact]
        public void EveryConcreteProtocolModelRoundTripsRepresentativeBoundaryValues()
        {
            foreach (var type in ProtocolTypes)
            {
                var model = CreateObject(type, new HashSet<Type>());
                var json = Serialize(type, model);

                AssertWireProperties(type, model, json);

                var restored = Deserialize(type, json);
                Assert.NotNull(restored);
                AssertEquivalent(model, restored, type, type.FullName ?? type.Name);
            }
        }

        [Fact]
        public void EveryConcreteProtocolModelAcceptsNumericValuesAsStrings()
        {
            foreach (var type in ProtocolTypes)
            {
                var model = CreateObject(type, new HashSet<Type>());
                var json = Serialize(type, model);
                var numericStrings = ConvertNumbersToStrings(json);
                var restored = Deserialize(type, numericStrings);

                Assert.NotNull(restored);
                AssertEquivalent(model, restored, type, type.FullName ?? type.Name);
            }
        }

        [Fact]
        public void GenericActionEnvelopeRoundTripsNestedRequest()
        {
            var type = typeof(WSActionReq<WSQueryReq>);
            var model = new WSActionReq<WSQueryReq>
            {
                Action = WSAction.Query,
                Args = new WSQueryReq
                {
                    ReqId = ulong.MaxValue,
                    Sql = "select \"中文\" from t where c = 'quote'"
                }
            };

            var json = Serialize(type, model);
            Assert.Contains("\"action\":\"query\"", json, StringComparison.Ordinal);
            Assert.Contains("\"req_id\":18446744073709551615", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Args", json, StringComparison.Ordinal);

            var restored = Deserialize(type, json);
            AssertEquivalent(model, restored, type, type.FullName ?? type.Name);
        }

        [Fact]
        public void OptionalListInstancesUsesNullOmissionButPreservesFalse()
        {
            var request = new WSConnReq
            {
                ReqId = 1,
                User = "root",
                Password = "taosdata",
                Db = "db"
            };

            using (var omitted = JsonDocument.Parse(WsJson.Serialize(request)))
            {
                Assert.False(omitted.RootElement.TryGetProperty("list_instances", out _));
            }

            request.ListInstances = false;
            using (var explicitFalse = JsonDocument.Parse(WsJson.Serialize(request)))
            {
                Assert.True(explicitFalse.RootElement.TryGetProperty("list_instances", out var value));
                Assert.False(value.GetBoolean());
            }
        }

        [Fact]
        public void ByteArrayConverterAcceptsBothWireRepresentationsAndRejectsInvalidValues()
        {
            var numericArray = WsJson.Deserialize<WSQueryResp>(
                "{\"fields_types\":[0,1,127,255],\"fields_precisions\":[0],\"fields_scales\":[255]}");
            Assert.Equal(new byte[] { 0, 1, 127, 255 }, numericArray.FieldsTypes);
            Assert.Equal(new byte[] { 255 }, numericArray.FieldsScales);

            var base64 = WsJson.Deserialize<WSQueryResp>(
                "{\"fields_types\":\"AAH//w==\",\"fields_precisions\":\"AA==\"}");
            Assert.Equal(new byte[] { 0, 1, 255, 255 }, base64.FieldsTypes);

            Assert.Throws<JsonException>(() => WsJson.Deserialize<WSQueryResp>(
                "{\"fields_types\":[-1]}"));
            Assert.Throws<JsonException>(() => WsJson.Deserialize<WSQueryResp>(
                "{\"fields_types\":[256]}"));
            Assert.ThrowsAny<Exception>(() => WsJson.Deserialize<WSQueryResp>(
                "{\"fields_types\":\"not-base64\"}"));
        }

        [Fact]
        public void JsonOptionsAcceptCommentsTrailingCommasCaseDifferencesAndNumericStrings()
        {
            var response = WsJson.Deserialize<WSBaseResp>(
                "{/* comment */\"CODE\":\"0\",\"MESSAGE\":\"ok\",\"ACTION\":\"query\"," +
                "\"REQ_ID\":\"42\",\"TIMING\":\"7\",}");

            Assert.Equal(0, response.Code);
            Assert.Equal("ok", response.Message);
            Assert.Equal("query", response.Action);
            Assert.Equal((ulong)42, response.ReqId);
            Assert.Equal(7, response.Timing);
        }

        [Fact]
        public void ProductionAssemblyDoesNotReferenceNewtonsoftJson()
        {
            var references = typeof(WsJson).Assembly.GetReferencedAssemblies();
            Assert.DoesNotContain(references, reference =>
                string.Equals(reference.Name, "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData("{\"req_id\":18446744073709551615}", ulong.MaxValue)]
        [InlineData("{\"req_id\":\"18446744073709551615\"}", ulong.MaxValue)]
        [InlineData("{/* comment */\"req_id\":\"42\",}", 42UL)]
        public void RequestIdFastParserAcceptsWireCompatibleForms(string json, ulong expected)
        {
            var parser = typeof(BaseConnectionAsync).GetMethod("ReadResponseRequestId",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(parser);
            var actual = parser!.Invoke(null, new object[] { json });
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"req_id\":-1}")]
        [InlineData("{\"req_id\":\"not-a-number\"}")]
        public void RequestIdFastParserRejectsInvalidForms(string json)
        {
            var parser = typeof(BaseConnectionAsync).GetMethod("ReadResponseRequestId",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(parser);
            var exception = Assert.Throws<TargetInvocationException>(() =>
                parser!.Invoke(null, new object[] { json }));
            Assert.IsType<JsonException>(exception.InnerException);
        }

        private static Type[] GetProtocolTypes()
        {
            return typeof(WSBaseResp).Assembly.GetTypes()
                .Where(type => type.Namespace == ProtocolNamespace &&
                               type.IsClass &&
                               !type.IsAbstract &&
                               !type.IsGenericTypeDefinition)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }

        private static PropertyInfo[] GetSerializableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetIndexParameters().Length == 0 &&
                                   property.GetMethod != null &&
                                   property.SetMethod != null)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();
        }

        private static object CreateObject(Type type, HashSet<Type> activeTypes)
        {
            Assert.False(type.ContainsGenericParameters, $"Open generic type {type} cannot be tested.");
            Assert.True(activeTypes.Add(type), $"Recursive protocol model graph detected at {type}.");

            try
            {
                var instance = Activator.CreateInstance(type);
                Assert.NotNull(instance);

                foreach (var property in GetSerializableProperties(type))
                {
                    property.SetValue(instance, CreateValue(property.PropertyType, activeTypes));
                }

                return instance!;
            }
            finally
            {
                activeTypes.Remove(type);
            }
        }

        private static object? CreateValue(Type type, HashSet<Type> activeTypes)
        {
            var nullableType = Nullable.GetUnderlyingType(type);
            if (nullableType != null)
            {
                return CreateValue(nullableType, activeTypes);
            }

            if (type == typeof(string)) return "value 中文 \"quoted\" \\ slash\nline";
            if (type == typeof(bool)) return true;
            if (type == typeof(byte)) return byte.MaxValue;
            if (type == typeof(sbyte)) return sbyte.MinValue;
            if (type == typeof(short)) return short.MinValue;
            if (type == typeof(ushort)) return ushort.MaxValue;
            if (type == typeof(int)) return int.MinValue + 123;
            if (type == typeof(uint)) return uint.MaxValue - 123;
            if (type == typeof(long)) return long.MinValue + 123;
            if (type == typeof(ulong)) return ulong.MaxValue;
            if (type == typeof(float)) return -1234.5f;
            if (type == typeof(double)) return 987654.125d;
            if (type == typeof(decimal)) return -123456789.0123456789m;
            if (type == typeof(byte[])) return new byte[] { 0, 1, 127, 255 };
            if (type == typeof(Guid)) return Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

            if (type.IsArray)
            {
                var elementType = type.GetElementType()!;
                var values = Array.CreateInstance(elementType, 2);
                values.SetValue(CreateValue(elementType, activeTypes), 0);
                values.SetValue(CreateValue(elementType, activeTypes), 1);
                return values;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type)!;
                list.Add(CreateValue(elementType, activeTypes));
                list.Add(CreateValue(elementType, activeTypes));
                return list;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var arguments = type.GetGenericArguments();
                Assert.Equal(typeof(string), arguments[0]);
                var dictionary = (IDictionary)Activator.CreateInstance(type)!;
                dictionary.Add("key", CreateValue(arguments[1], activeTypes));
                dictionary.Add("中文", CreateValue(arguments[1], activeTypes));
                return dictionary;
            }

            if (type.IsEnum)
            {
                return Enum.GetValues(type).GetValue(0);
            }

            if (type == typeof(object)) return "object-value";
            if (type.IsClass) return CreateObject(type, activeTypes);

            throw new InvalidOperationException($"No representative value defined for {type}.");
        }

        private static string Serialize(Type type, object value)
        {
            var method = typeof(WsJson).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(candidate => candidate.Name == "Serialize" && candidate.IsGenericMethodDefinition);
            return (string)method.MakeGenericMethod(type).Invoke(null, new[] { value })!;
        }

        private static object Deserialize(Type type, string json)
        {
            var method = typeof(WsJson).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(candidate => candidate.Name == "Deserialize" && candidate.IsGenericMethodDefinition);
            return method.MakeGenericMethod(type).Invoke(null, new object[] { json })!;
        }

        private static void AssertWireProperties(Type type, object model, string json)
        {
            using var document = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

            foreach (var property in GetSerializableProperties(type))
            {
                var wireName = property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name;
                var value = property.GetValue(model);
                var ignore = property.GetCustomAttribute<JsonIgnoreAttribute>();
                var omitted = value == null && ignore?.Condition == JsonIgnoreCondition.WhenWritingNull;

                Assert.Equal(!omitted, document.RootElement.TryGetProperty(wireName, out _));
                if (!omitted && !string.Equals(property.Name, wireName, StringComparison.Ordinal))
                {
                    Assert.False(document.RootElement.TryGetProperty(property.Name, out _),
                        $"CLR property {property.Name} leaked into {type.FullName} JSON.");
                }
            }
        }

        private static void AssertEquivalent(object? expected, object? actual, Type type, string path)
        {
            if (expected == null)
            {
                Assert.Null(actual);
                return;
            }

            Assert.NotNull(actual);
            var effectiveType = Nullable.GetUnderlyingType(type) ?? type;

            if (effectiveType == typeof(byte[]))
            {
                Assert.Equal((byte[])expected, (byte[])actual!);
                return;
            }

            if (effectiveType.IsArray)
            {
                var expectedArray = (Array)expected;
                var actualArray = (Array)actual!;
                Assert.Equal(expectedArray.Length, actualArray.Length);
                var elementType = effectiveType.GetElementType()!;
                for (var i = 0; i < expectedArray.Length; i++)
                {
                    AssertEquivalent(expectedArray.GetValue(i), actualArray.GetValue(i), elementType,
                        $"{path}[{i}]");
                }

                return;
            }

            if (effectiveType.IsGenericType && effectiveType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var expectedList = (IList)expected;
                var actualList = (IList)actual!;
                Assert.Equal(expectedList.Count, actualList.Count);
                var elementType = effectiveType.GetGenericArguments()[0];
                for (var i = 0; i < expectedList.Count; i++)
                {
                    AssertEquivalent(expectedList[i], actualList[i], elementType, $"{path}[{i}]");
                }

                return;
            }

            if (effectiveType.IsGenericType &&
                effectiveType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var expectedDictionary = (IDictionary)expected;
                var actualDictionary = (IDictionary)actual!;
                Assert.Equal(expectedDictionary.Count, actualDictionary.Count);
                var valueType = effectiveType.GetGenericArguments()[1];
                foreach (DictionaryEntry entry in expectedDictionary)
                {
                    Assert.True(actualDictionary.Contains(entry.Key), $"Missing dictionary key at {path}.");
                    AssertEquivalent(entry.Value, actualDictionary[entry.Key], valueType,
                        $"{path}.{entry.Key}");
                }

                return;
            }

            if (IsScalar(effectiveType))
            {
                Assert.Equal(expected, actual);
                return;
            }

            foreach (var property in GetSerializableProperties(effectiveType))
            {
                AssertEquivalent(property.GetValue(expected), property.GetValue(actual), property.PropertyType,
                    $"{path}.{property.Name}");
            }
        }

        private static bool IsScalar(Type type)
        {
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
                   type == typeof(Guid);
        }

        private static string ConvertNumbersToStrings(string json)
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteNumbersAsStrings(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteNumbersAsStrings(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        WriteNumbersAsStrings(property.Value, writer);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteNumbersAsStrings(item, writer);
                    }

                    writer.WriteEndArray();
                    break;
                case JsonValueKind.Number:
                    writer.WriteStringValue(element.GetRawText());
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(element.GetString());
                    break;
                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;
                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;
                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported JSON token {element.ValueKind}.");
            }
        }
    }
}
