using System;

namespace Sync.Core.Schema
{
    public enum FieldCategory
    {
        UserOwned,
        UserPreference,
        ServerAuthoritative,
        Counter,
        Scalar,
    }

    public static class FieldCategoryExtensions
    {
        public static string ToStorageName(this FieldCategory category)
        {
            switch (category)
            {
                case FieldCategory.UserOwned: return "user_owned";
                case FieldCategory.UserPreference: return "user_preference";
                case FieldCategory.ServerAuthoritative: return "server_authoritative";
                case FieldCategory.Counter: return "counter";
                case FieldCategory.Scalar: return "scalar";
                default: throw new ArgumentOutOfRangeException(nameof(category), category, null);
            }
        }
    }
}
