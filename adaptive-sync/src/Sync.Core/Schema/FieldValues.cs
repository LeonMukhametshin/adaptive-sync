using System;
using System.Globalization;
using System.Text.Json;

namespace Sync.Core.Schema
{
    public static class FieldValues
    {
        public static object? Normalize(FieldType type, object? value)
        {
            if (value == null || value is DBNull) return null;
            if (value is JsonElement json) return FromJson(type, json);

            switch (type)
            {
                case FieldType.Text:
                    return value as string ?? throw Mismatch(type, value);
                case FieldType.Integer:
                    switch (value)
                    {
                        case long l: return l;
                        case int i: return (long)i;
                        case short s: return (long)s;
                        default: throw Mismatch(type, value);
                    }
                case FieldType.Boolean:
                    switch (value)
                    {
                        case bool b: return b;
                        case long l when l == 0 || l == 1: return l == 1;
                        case int i when i == 0 || i == 1: return i == 1;
                        default: throw Mismatch(type, value);
                    }
                case FieldType.Uuid:
                    switch (value)
                    {
                        case Guid g: return g;
                        case string s when Guid.TryParse(s, out var parsed): return parsed;
                        default: throw Mismatch(type, value);
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        public static bool AreEqual(FieldType type, object? left, object? right) =>
            Equals(Normalize(type, left), Normalize(type, right));

        public static object? ToStorage(FieldType type, object? value)
        {
            var normalized = Normalize(type, value);
            switch (normalized)
            {
                case null: return null;
                case bool b: return b ? 1L : 0L;
                case Guid g: return g.ToString("D");
                default: return normalized;
            }
        }

        private static object? FromJson(FieldType type, JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Null || json.ValueKind == JsonValueKind.Undefined) return null;

            switch (type)
            {
                case FieldType.Text when json.ValueKind == JsonValueKind.String:
                    return json.GetString();
                case FieldType.Integer when json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out var l):
                    return l;
                case FieldType.Boolean when json.ValueKind == JsonValueKind.True || json.ValueKind == JsonValueKind.False:
                    return json.GetBoolean();
                case FieldType.Uuid when json.ValueKind == JsonValueKind.String && json.TryGetGuid(out var g):
                    return g;
                default:
                    throw Mismatch(type, json.GetRawText());
            }
        }

        private static FormatException Mismatch(FieldType type, object value) =>
            new FormatException(string.Format(CultureInfo.InvariantCulture,
                "Value '{0}' of type {1} is not a valid {2}.", value, value.GetType().Name, type.ToStorageName()));
    }
}
