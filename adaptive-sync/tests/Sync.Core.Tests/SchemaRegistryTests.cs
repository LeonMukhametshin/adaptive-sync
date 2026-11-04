using Sync.Core.Schema;

namespace Sync.Core.Tests;

public class SchemaRegistryTests
{
    private static readonly SchemaRegistry Registry = SchemaVersions.Registry;

    [Fact]
    public void KnownFields_FormStrictChainV1V2V3()
    {
        var k1 = Registry.KnownFields(SchemaVersions.V1).ToHashSet();
        var k2 = Registry.KnownFields(SchemaVersions.V2).ToHashSet();
        var k3 = Registry.KnownFields(SchemaVersions.V3).ToHashSet();

        Assert.True(k1.IsProperSubsetOf(k2), "K(v1) must be a proper subset of K(v2)");
        Assert.True(k2.IsProperSubsetOf(k3), "K(v2) must be a proper subset of K(v3)");
    }

    [Fact]
    public void KnownFields_OfLatestVersion_AreAllFields()
    {
        Assert.Equal(
            Registry.AllFields.Select(f => f.Ref).ToHashSet(),
            Registry.KnownFields(Registry.LatestVersion).ToHashSet());
    }

    [Theory]
    [InlineData(1, new[] { "player_profile.nickname", "player_profile.level", "player_profile.coins", "player_profile.settings_sound",
        "inventory_item.player_id", "inventory_item.item_type", "inventory_item.quantity" })]
    [InlineData(2, new[] { "player_profile.nickname", "player_profile.level", "player_profile.coins", "player_profile.settings_sound",
        "player_profile.avatar_id", "player_profile.premium_currency",
        "inventory_item.player_id", "inventory_item.item_type", "inventory_item.quantity", "inventory_item.durability" })]
    [InlineData(3, new[] { "player_profile.nickname", "player_profile.level", "player_profile.coins", "player_profile.settings_sound",
        "player_profile.avatar_id", "player_profile.premium_currency", "player_profile.region",
        "inventory_item.player_id", "inventory_item.item_type", "inventory_item.quantity", "inventory_item.durability",
        "item_modification.item_id", "item_modification.mod_type", "item_modification.mod_level" })]
    public void KnownFields_MatchDataModel(int version, string[] expected)
    {
        var actual = Registry.KnownFields(version).Select(f => f.ToString()).OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal), actual);
    }

    [Theory]
    [InlineData(1, "player_profile", "region", false)]
    [InlineData(2, "player_profile", "region", false)]
    [InlineData(3, "player_profile", "region", true)]
    [InlineData(1, "player_profile", "avatar_id", false)]
    [InlineData(2, "player_profile", "avatar_id", true)]
    [InlineData(2, "item_modification", "mod_type", false)]
    [InlineData(3, "item_modification", "mod_type", true)]
    [InlineData(1, "player_profile", "no_such_field", false)]
    public void IsKnown_ReflectsFieldVersion(int version, string table, string field, bool expected)
    {
        Assert.Equal(expected, Registry.IsKnown(version, table, field));
    }

    [Fact]
    public void TablesFor_HidesItemModificationFromOldClients()
    {
        Assert.Equal(new[] { "player_profile", "inventory_item" }, Registry.TablesFor(SchemaVersions.V2).Select(t => t.Name));
        Assert.Equal(
            new[] { "player_profile", "inventory_item", "item_modification" },
            Registry.TablesFor(SchemaVersions.V3).Select(t => t.Name));
    }

    [Theory]
    [InlineData("player_profile", "nickname", FieldCategory.UserOwned)]
    [InlineData("player_profile", "level", FieldCategory.ServerAuthoritative)]
    [InlineData("player_profile", "coins", FieldCategory.Counter)]
    [InlineData("player_profile", "settings_sound", FieldCategory.UserPreference)]
    [InlineData("player_profile", "avatar_id", FieldCategory.UserOwned)]
    [InlineData("player_profile", "premium_currency", FieldCategory.Counter)]
    [InlineData("player_profile", "region", FieldCategory.ServerAuthoritative)]
    [InlineData("inventory_item", "quantity", FieldCategory.Counter)]
    [InlineData("inventory_item", "durability", FieldCategory.Scalar)]
    public void Categories_MatchPlan(string table, string field, FieldCategory expected)
    {
        Assert.Equal(expected, Registry.GetField(table, field).Category);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void KnownFields_RejectsUnsupportedVersion(int version)
    {
        Assert.False(Registry.IsSupported(version));
        Assert.Throws<ArgumentOutOfRangeException>(() => Registry.KnownFields(version));
    }

    [Fact]
    public void Constructor_RejectsFieldOlderThanItsTable()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SchemaRegistry(2, new[]
        {
            new TableDefinition("t", 2, new[] { new FieldDefinition("t", "f", 1, FieldCategory.Scalar, FieldType.Text) }),
        }));
        Assert.Contains("t.f", ex.Message);
    }

    [Fact]
    public void Constructor_RejectsRequiredFieldAddedToExistingTable()
    {
        Assert.Throws<ArgumentException>(() => new SchemaRegistry(2, new[]
        {
            new TableDefinition("t", 1, new[] { new FieldDefinition("t", "f", 2, FieldCategory.Scalar, FieldType.Text, isRequired: true) }),
        }));
    }

    [Fact]
    public void Constructor_RejectsReferenceToLaterTable()
    {
        Assert.Throws<ArgumentException>(() => new SchemaRegistry(2, new[]
        {
            new TableDefinition("child", 1, new[] { new FieldDefinition("child", "parent_id", 1, FieldCategory.Scalar, FieldType.Uuid, references: "parent") }),
            new TableDefinition("parent", 1, Array.Empty<FieldDefinition>()),
        }));
    }

    [Fact]
    public void Constructor_RejectsServiceColumnNames()
    {
        Assert.Throws<ArgumentException>(() => new SchemaRegistry(1, new[]
        {
            new TableDefinition("t", 1, new[] { new FieldDefinition("t", "row_version", 1, FieldCategory.Scalar, FieldType.Integer) }),
        }));
    }
}
