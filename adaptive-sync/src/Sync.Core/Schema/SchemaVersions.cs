namespace Sync.Core.Schema
{
    public static class SchemaVersions
    {
        public const int V1 = 1;
        public const int V2 = 2;
        public const int V3 = 3;
        public const int Latest = V3;

        public const string PlayerProfile = "player_profile";
        public const string InventoryItem = "inventory_item";
        public const string ItemModification = "item_modification";

        public static SchemaRegistry Registry { get; } = Build();

        private static SchemaRegistry Build()
        {
            return new SchemaRegistry(Latest, new[]
            {
                new TableDefinition(PlayerProfile, V1, new[]
                {
                    new FieldDefinition(PlayerProfile, "nickname", V1, FieldCategory.UserOwned, FieldType.Text),
                    new FieldDefinition(PlayerProfile, "level", V1, FieldCategory.ServerAuthoritative, FieldType.Integer, defaultValue: 1L),
                    new FieldDefinition(PlayerProfile, "coins", V1, FieldCategory.Counter, FieldType.Integer, defaultValue: 0L),
                    new FieldDefinition(PlayerProfile, "settings_sound", V1, FieldCategory.UserPreference, FieldType.Boolean, defaultValue: true),
                    new FieldDefinition(PlayerProfile, "avatar_id", V2, FieldCategory.UserOwned, FieldType.Text),
                    new FieldDefinition(PlayerProfile, "premium_currency", V2, FieldCategory.Counter, FieldType.Integer, defaultValue: 0L),
                    new FieldDefinition(PlayerProfile, "region", V3, FieldCategory.ServerAuthoritative, FieldType.Text),
                }),
                new TableDefinition(InventoryItem, V1, new[]
                {
                    new FieldDefinition(InventoryItem, "player_id", V1, FieldCategory.Scalar, FieldType.Uuid, isRequired: true, references: PlayerProfile),
                    new FieldDefinition(InventoryItem, "item_type", V1, FieldCategory.Scalar, FieldType.Text),
                    new FieldDefinition(InventoryItem, "quantity", V1, FieldCategory.Counter, FieldType.Integer, defaultValue: 0L),
                    new FieldDefinition(InventoryItem, "durability", V2, FieldCategory.Scalar, FieldType.Integer, defaultValue: 100L),
                }),
                new TableDefinition(ItemModification, V3, new[]
                {
                    new FieldDefinition(ItemModification, "item_id", V3, FieldCategory.Scalar, FieldType.Uuid, isRequired: true, references: InventoryItem),
                    new FieldDefinition(ItemModification, "mod_type", V3, FieldCategory.Scalar, FieldType.Text),
                    new FieldDefinition(ItemModification, "mod_level", V3, FieldCategory.Scalar, FieldType.Integer, defaultValue: 1L),
                }),
            });
        }
    }
}
