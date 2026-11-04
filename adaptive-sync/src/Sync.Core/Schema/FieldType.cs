using System;

namespace Sync.Core.Schema
{
    public enum FieldType
    {
        Text,
        Integer,
        Boolean,
        Uuid,
    }

    public static class FieldTypeExtensions
    {
        public static string ToStorageName(this FieldType type)
        {
            switch (type)
            {
                case FieldType.Text: return "text";
                case FieldType.Integer: return "integer";
                case FieldType.Boolean: return "boolean";
                case FieldType.Uuid: return "uuid";
                default: throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }
    }
}
