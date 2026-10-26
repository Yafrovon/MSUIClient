using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSUIClient.Creator;

// ═══════════════════════════════════════════════════════════════════════════
// CREATOR JSON — the one serializer configuration for everything Creator Mode
// saves (shared_docs/SPELL_CREATOR_PLAN.md D7: the session entry is the save
// file, the push payload and the undo snapshot, so all three must read and
// write the SAME shapes).
//
//   • vectors are short arrays: Vector3 (1, 0, 0.5) is [1, 0, 0.5];
//   • enums are their NAMES, so reordering an enum never reinterprets a file;
//   • public fields are included (the Sketch model is field-based on purpose);
//   • computed, get-only properties are NOT written (SketchDoc.Length, IsMissile, …);
//   • null members are omitted, and a member a newer build added is simply
//     absent from an older file and keeps its default — files stay forward- and
//     backward-readable without a migration.
// ═══════════════════════════════════════════════════════════════════════════
public static class CreatorJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            IgnoreReadOnlyProperties = true,
            IgnoreReadOnlyFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new Vector2ArrayConverter());
        options.Converters.Add(new Vector3ArrayConverter());
        options.Converters.Add(new Vector4ArrayConverter());
        return options;
    }

    private static float[] ReadFloats(ref Utf8JsonReader reader, int count)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("a vector must be a JSON array");
        var values = new float[count];
        int n = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (n < count) values[n] = reader.GetSingle();
            n++;
        }
        if (n != count) throw new JsonException($"a {count}-vector had {n} components");
        return values;
    }

    private static void WriteFloats(Utf8JsonWriter writer, ReadOnlySpan<float> values)
    {
        writer.WriteStartArray();
        foreach (float v in values) writer.WriteNumberValue(v);
        writer.WriteEndArray();
    }

    private sealed class Vector2ArrayConverter : JsonConverter<Vector2>
    {
        public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            float[] v = ReadFloats(ref reader, 2);
            return new Vector2(v[0], v[1]);
        }

        public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options) =>
            WriteFloats(writer, [value.X, value.Y]);
    }

    private sealed class Vector3ArrayConverter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            float[] v = ReadFloats(ref reader, 3);
            return new Vector3(v[0], v[1], v[2]);
        }

        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options) =>
            WriteFloats(writer, [value.X, value.Y, value.Z]);
    }

    private sealed class Vector4ArrayConverter : JsonConverter<Vector4>
    {
        public override Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            float[] v = ReadFloats(ref reader, 4);
            return new Vector4(v[0], v[1], v[2], v[3]);
        }

        public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options) =>
            WriteFloats(writer, [value.X, value.Y, value.Z, value.W]);
    }
}
