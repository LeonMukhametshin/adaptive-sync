using Sync.Core.Protocol;
using Sync.Core.Strategies;

namespace Sync.Oracle;

public enum OracleOperation
{
    Insert,
    Update,
    Delete,
}

public sealed class OracleEvent
{
    public const string ServerAuthor = "server";

    public string Author { get; init; } = ServerAuthor;

    public int SchemaVersion { get; init; }

    public OracleOperation Operation { get; init; }

    public string Table { get; init; } = string.Empty;

    public Guid RecordId { get; init; }

    public IReadOnlyList<FieldChange> Changes { get; init; } = Array.Empty<FieldChange>();

    public long MadeAt { get; init; }

    public long SeenAt { get; init; }

    public long DeliveredAt { get; init; }

    public bool IsServer => Author == ServerAuthor;
}

public sealed class OracleRecord
{
    public string Table { get; init; } = string.Empty;

    public Guid RecordId { get; init; }

    public Dictionary<string, object?> Values { get; init; } = new();

    public bool IsDeleted { get; init; }
}

public sealed class OracleInput
{
    public List<OracleRecord> InitialRecords { get; init; } = new();

    public List<OracleEvent> Events { get; init; } = new();
}

public sealed class ExpectedRecord
{
    public string Table { get; init; } = string.Empty;

    public Guid RecordId { get; init; }

    public bool IsDeleted { get; set; }

    public bool IsTombstone { get; set; }

    public Dictionary<string, object?> Values { get; init; } = new();
}

public sealed record ChangeClassification(int EventIndex, string Table, Guid RecordId, string? Field, SyncSituation Situation);

public sealed class OracleResult
{
    public OracleResult(IReadOnlyDictionary<(string Table, Guid Id), ExpectedRecord> records, IReadOnlyList<ChangeClassification> classifications)
    {
        Records = records;
        Classifications = classifications;
    }

    public IReadOnlyDictionary<(string Table, Guid Id), ExpectedRecord> Records { get; }

    public IReadOnlyList<ChangeClassification> Classifications { get; }

    public ExpectedRecord Record(string table, Guid id) =>
        Records.TryGetValue((table, id), out var record)
            ? record
            : throw new KeyNotFoundException($"Oracle has no record {table}/{id}.");

    public object? Value(string table, Guid id, string field) => Record(table, id).Values[field];
}
