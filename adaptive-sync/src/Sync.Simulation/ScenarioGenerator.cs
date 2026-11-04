using Sync.Core.Protocol;
using Sync.Core.Schema;
using Sync.Oracle;

namespace Sync.Simulation;

public sealed record GeneratorOptions
{
    public int Seed { get; init; } = 1;

    public int Profiles { get; init; } = 4;

    public int ItemsPerProfile { get; init; } = 2;

    public int Clients { get; init; } = 6;

    public double OldClientShare { get; init; } = 0.5;

    public int Rounds { get; init; } = 4;

    public int OfflineOperations { get; init; } = 3;

    public double ConflictRate { get; init; } = 0.3;

    public double ServerChangeRate { get; init; } = 0.4;

    public double DeleteRate { get; init; } = 0.05;

    public double InsertRate { get; init; } = 0.05;

    public string Name =>
        $"seed={Seed} old={OldClientShare:0.##} offline={OfflineOperations} conflict={ConflictRate:0.##}";
}

public sealed class ScenarioGenerator(SchemaRegistry registry)
{
    private const string Profile = SchemaVersions.PlayerProfile;
    private const string Item = SchemaVersions.InventoryItem;
    private const string Modification = SchemaVersions.ItemModification;

    public Scenario Generate(GeneratorOptions options)
    {
        var state = new GeneratorState(registry, new Random(options.Seed));
        var initial = state.CreateInitialRecords(options);
        var events = new List<OracleEvent>();
        var clients = CreateClients(options);

        var horizon = 0L;
        foreach (var client in clients)
        {
            var time = 1L + state.Next(3);
            for (var round = 0; round < options.Rounds; round++)
            {
                var seen = time;
                var made = seen;
                for (var op = 0; op < options.OfflineOperations; op++)
                {
                    made++;
                    var e = state.ClientEvent(client.Id, client.Version, options, seen, made);
                    if (e != null)
                        events.Add(e);
                }
                var delivered = made + 1 + state.Next(options.OfflineOperations + 1);
                for (var i = events.Count - 1; i >= 0 && events[i].Author == client.Id && events[i].SeenAt == seen; i--)
                    events[i] = With(events[i], delivered);
                time = delivered + 1 + state.Next(3);
                horizon = Math.Max(horizon, delivered);
            }
        }

        for (var t = 1L; t <= horizon; t++)
        {
            if (state.NextDouble() < options.ServerChangeRate)
            {
                var e = state.ServerEvent(t);
                if (e != null)
                    events.Add(e);
            }
        }

        var ordered = events
            .Select((e, i) => (e, i))
            .OrderBy(p => p.e.DeliveredAt)
            .ThenBy(p => p.e.IsServer ? 0 : 1)
            .ThenBy(p => p.e.Author, StringComparer.Ordinal)
            .ThenBy(p => p.e.MadeAt)
            .ThenBy(p => p.i)
            .Select(p => p.e)
            .ToList();

        CounterDeltas.MakeConsistent(registry, initial, ordered);
        return new Scenario(options.Name, new OracleInput { InitialRecords = initial, Events = ordered });
    }

    private static List<(string Id, int Version)> CreateClients(GeneratorOptions options)
    {
        var old = (int)Math.Round(options.OldClientShare * options.Clients, MidpointRounding.AwayFromZero);
        return Enumerable.Range(0, options.Clients)
            .Select(i => ("c" + i, i < old ? (i % 2 == 0 ? SchemaVersions.V1 : SchemaVersions.V2) : SchemaVersions.V3))
            .ToList();
    }

    private static OracleEvent With(OracleEvent e, long delivered) =>
        new()
        {
            Author = e.Author,
            SchemaVersion = e.SchemaVersion,
            Operation = e.Operation,
            Table = e.Table,
            RecordId = e.RecordId,
            Changes = e.Changes,
            MadeAt = e.MadeAt,
            SeenAt = e.SeenAt,
            DeliveredAt = delivered,
        };

    private sealed class GeneratorState(SchemaRegistry registry, Random random)
    {
        private readonly Dictionary<(string Table, Guid Id), Dictionary<string, object?>> _values = new();
        private readonly Dictionary<string, List<Guid>> _live = new() { [Profile] = new(), [Item] = new(), [Modification] = new() };
        private int _tokens;

        public int Next(int max) => random.Next(max);

        public double NextDouble() => random.NextDouble();

        public List<OracleRecord> CreateInitialRecords(GeneratorOptions options)
        {
            var records = new List<OracleRecord>();
            for (var p = 0; p < options.Profiles; p++)
            {
                var profile = NewId();
                records.Add(Track(Profile, profile, new Dictionary<string, object?>
                {
                    ["nickname"] = Token("nick"), ["level"] = 1L + random.Next(10), ["coins"] = 100L * random.Next(1, 10),
                    ["settings_sound"] = random.Next(2) == 0, ["avatar_id"] = Token("avatar"),
                    ["premium_currency"] = (long)random.Next(0, 3) * 10, ["region"] = random.Next(2) == 0 ? "eu" : "us",
                }));
                for (var i = 0; i < options.ItemsPerProfile; i++)
                {
                    var item = NewId();
                    records.Add(Track(Item, item, new Dictionary<string, object?>
                    {
                        ["player_id"] = profile, ["item_type"] = Token("item"), ["quantity"] = 1L + random.Next(5),
                        ["durability"] = random.Next(2) == 0 ? 100L : 50L + random.Next(50),
                    }));
                    if (random.Next(2) == 0)
                    {
                        records.Add(Track(Modification, NewId(), new Dictionary<string, object?>
                        {
                            ["item_id"] = item, ["mod_type"] = Token("mod"), ["mod_level"] = 1L + random.Next(3),
                        }));
                    }
                }
            }
            return records;
        }

        public OracleEvent? ClientEvent(string author, int version, GeneratorOptions options, long seen, long made)
        {
            var tables = registry.TablesFor(version).Select(t => t.Name).Where(t => _live[t].Count > 0).ToList();
            if (tables.Count == 0) return null;
            var table = tables[random.Next(tables.Count)];
            var hot = random.NextDouble() < options.ConflictRate;
            var roll = random.NextDouble();

            if (roll < options.InsertRate && table != Profile)
                return Insert(author, version, table, seen, made);

            var id = hot ? _live[table][0] : _live[table][random.Next(_live[table].Count)];
            if (roll < options.InsertRate + options.DeleteRate && !hot)
            {
                Delete(table, id);
                return Event(author, version, OracleOperation.Delete, table, id, seen, made, new List<FieldChange>());
            }

            var fields = registry.GetTable(table).FieldsFor(version).Where(f => f.References == null).ToList();
            var count = 1 + random.Next(Math.Min(2, fields.Count));
            var chosen = hot
                ? fields.Take(count).ToList()
                : fields.OrderBy(_ => random.Next()).Take(count).ToList();
            return Event(author, version, OracleOperation.Update, table, id, seen, made, chosen.Select(f => Change(table, id, f)).ToList());
        }

        public OracleEvent? ServerEvent(long at)
        {
            var table = random.Next(3) switch { 0 => Profile, 1 => Item, _ => Modification };
            if (_live[table].Count == 0) return null;
            var id = _live[table][random.Next(_live[table].Count)];
            var fields = registry.GetTable(table).Fields.Where(f => f.References == null).ToList();
            var preferred = fields.Where(f => f.IntroducedIn > SchemaVersions.V1 || f.Category == FieldCategory.ServerAuthoritative).ToList();
            var field = preferred.Count > 0 && random.NextDouble() < 0.7 ? preferred[random.Next(preferred.Count)] : fields[random.Next(fields.Count)];
            return Event(OracleEvent.ServerAuthor, SchemaVersions.Latest, OracleOperation.Update, table, id, at, at,
                new List<FieldChange> { Change(table, id, field) }, at);
        }

        private OracleEvent Insert(string author, int version, string table, long seen, long made)
        {
            var id = NewId();
            var values = table == Item
                ? new Dictionary<string, object?>
                {
                    ["player_id"] = _live[Profile][random.Next(_live[Profile].Count)], ["item_type"] = Token("item"), ["quantity"] = 1L,
                }
                : new Dictionary<string, object?>
                {
                    ["item_id"] = _live[Item][random.Next(_live[Item].Count)], ["mod_type"] = Token("mod"), ["mod_level"] = 1L,
                };
            var tableDefinition = registry.GetTable(table);
            var full = tableDefinition.Fields.ToDictionary(f => f.Name, f => values.TryGetValue(f.Name, out var v) ? v : f.DefaultValue);
            _values[(table, id)] = full;
            _live[table].Add(id);
            return Event(author, version, OracleOperation.Insert, table, id, seen, made,
                values.Select(p => new FieldChange { Field = p.Key, OldValue = null, NewValue = p.Value }).ToList());
        }

        private FieldChange Change(string table, Guid id, FieldDefinition field)
        {
            var values = _values[(table, id)];
            var old = values[field.Name];
            object? value = field.Type switch
            {
                FieldType.Integer when field.Category == FieldCategory.Counter => (long)(old ?? 0L) + 1 + random.Next(20),
                FieldType.Integer => 1L + random.Next(100),
                FieldType.Boolean => !(bool)(old ?? false),
                FieldType.Text => Token(field.Name),
                _ => old,
            };
            values[field.Name] = value;
            return new FieldChange { Field = field.Name, OldValue = old, NewValue = value };
        }

        private void Delete(string table, Guid id)
        {
            _live[table].Remove(id);
            if (_live[table].Count == 0)
                _live[table].Add(id);
        }

        private static OracleEvent Event(
            string author, int version, OracleOperation operation, string table, Guid id, long seen, long made,
            List<FieldChange> changes, long? delivered = null) =>
            new()
            {
                Author = author,
                SchemaVersion = version,
                Operation = operation,
                Table = table,
                RecordId = id,
                Changes = changes,
                SeenAt = seen,
                MadeAt = made,
                DeliveredAt = delivered ?? made,
            };

        private OracleRecord Track(string table, Guid id, Dictionary<string, object?> values)
        {
            _values[(table, id)] = new Dictionary<string, object?>(values);
            _live[table].Add(id);
            return new OracleRecord { Table = table, RecordId = id, Values = values };
        }

        private Guid NewId()
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes);
        }

        private string Token(string prefix) => prefix + "_" + (++_tokens);
    }
}
