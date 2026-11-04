using System.Text.Json;
using Sync.Core.Schema;

namespace Sync.Core.Tests;

public class FieldValuesTests
{
    private static readonly Guid Id = Guid.Parse("6f9619ff-8b86-4d11-b42d-00c04fc964ff");

    public static TheoryData<FieldType, object?, object?> NormalizeCases => new()
    {
        { FieldType.Text, "neo", "neo" },
        { FieldType.Text, null, null },
        { FieldType.Integer, 5, 5L },
        { FieldType.Integer, 7L, 7L },
        { FieldType.Boolean, true, true },
        { FieldType.Boolean, 0L, false },
        { FieldType.Boolean, 1L, true },
        { FieldType.Uuid, Id, Id },
        { FieldType.Uuid, Id.ToString(), Id },
        { FieldType.Uuid, DBNull.Value, null },
    };

    [Theory]
    [MemberData(nameof(NormalizeCases))]
    public void Normalize_ConvertsToCanonicalType(FieldType type, object? input, object? expected)
    {
        Assert.Equal(expected, FieldValues.Normalize(type, input));
    }

    [Theory]
    [InlineData(FieldType.Text, "\"neo\"", "neo")]
    [InlineData(FieldType.Integer, "42", 42L)]
    [InlineData(FieldType.Boolean, "false", false)]
    [InlineData(FieldType.Text, "null", null)]
    public void Normalize_ReadsJsonElements(FieldType type, string json, object? expected)
    {
        Assert.Equal(expected, FieldValues.Normalize(type, JsonSerializer.Deserialize<JsonElement>(json)));
    }

    [Fact]
    public void Normalize_ReadsJsonUuid()
    {
        var element = JsonSerializer.Deserialize<JsonElement>($"\"{Id}\"");

        Assert.Equal(Id, FieldValues.Normalize(FieldType.Uuid, element));
    }

    [Theory]
    [InlineData(FieldType.Integer, "abc")]
    [InlineData(FieldType.Boolean, 2L)]
    [InlineData(FieldType.Uuid, "not-a-guid")]
    [InlineData(FieldType.Text, 5L)]
    public void Normalize_RejectsWrongType(FieldType type, object value)
    {
        Assert.Throws<FormatException>(() => FieldValues.Normalize(type, value));
    }

    [Fact]
    public void Normalize_RejectsFractionalJsonInteger()
    {
        Assert.Throws<FormatException>(() =>
            FieldValues.Normalize(FieldType.Integer, JsonSerializer.Deserialize<JsonElement>("1.5")));
    }

    [Fact]
    public void ToStorage_UsesSqliteRepresentation()
    {
        Assert.Equal(1L, FieldValues.ToStorage(FieldType.Boolean, true));
        Assert.Equal(Id.ToString("D"), FieldValues.ToStorage(FieldType.Uuid, Id));
        Assert.Null(FieldValues.ToStorage(FieldType.Text, null));
    }
}
