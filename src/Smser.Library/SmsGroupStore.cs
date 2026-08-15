using Azure;
using Azure.Data.Tables;

namespace Smser.Library;

/// <summary>
/// Rosters in Azure Table Storage — one entity per saved list, keyed by its
/// <see cref="ShortId"/>.
///
/// Table rather than Blob: a roster is a handful of small fields fetched whole by key,
/// which is exactly the access pattern tables are cheapest at, and it keeps the stored
/// shape inspectable in Storage Explorer instead of being an opaque JSON blob. The
/// original app used blobs and serialised the whole record — including a base64 QR image
/// — into one; the image is regenerated per request here instead, which is what keeps
/// each entity a few kilobytes.
///
/// Everything lands in one partition. That caps write throughput at a single partition
/// server, which for an app where a write is a person pressing Generate is not a
/// constraint worth sharding around — and it makes a retention sweep a single partition
/// scan rather than a table scan.
/// </summary>
public sealed class SmsGroupStore
{
    private const string TableName = "rosters";
    private const string PartitionKey = "roster";

    private const string GroupNameColumn = "GroupName";
    private const string RawTextColumn = "RawText";
    private const string NumbersColumn = "Numbers";
    private const string CreatedColumn = "CreatedTs";
    private const string UpdatedColumn = "UpdatedTs";

    /// <summary>
    /// Separator for the numbers column. Newline rather than comma so the stored value
    /// is the same shape as the textarea it came from and reads correctly in a storage
    /// browser; normalised numbers are digits only, so it can never appear inside one.
    /// </summary>
    private const char NumbersSeparator = '\n';

    private readonly TableServiceClient _tables;

    public SmsGroupStore(TableServiceClient tables) => _tables = tables;

    /// <summary>
    /// Writes a new roster under a freshly minted id and returns it.
    ///
    /// Insert, not upsert, and a colliding id is retried. At 41.7 bits a collision is not
    /// going to happen, but "not going to happen" and "silently overwrites a stranger's
    /// roster if it does" are different claims, and only one of them is enforced by the
    /// code.
    /// </summary>
    public async Task<SmsGroup> CreateAsync(string groupName, string rawText, IReadOnlyList<string> numbers, CancellationToken cancellationToken = default)
    {
        Validate(groupName, rawText, numbers);

        var table = _tables.GetTableClient(TableName);
        var now = DateTimeOffset.UtcNow;

        for (var attempt = 0; ; attempt++)
        {
            var id = ShortId.Create();
            var entity = new TableEntity(PartitionKey, id)
            {
                [GroupNameColumn] = groupName,
                [RawTextColumn] = rawText,
                [NumbersColumn] = string.Join(NumbersSeparator, numbers),
                [CreatedColumn] = now,
                [UpdatedColumn] = now
            };

            try
            {
                await AddWithTableCreateAsync(table, entity, cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 409 && attempt < 4)
            {
                continue;
            }

            return new SmsGroup
            {
                Id = id,
                GroupName = groupName,
                RawText = rawText,
                Numbers = numbers,
                CreatedAt = now,
                UpdatedAt = now
            };
        }
    }

    /// <summary>
    /// Overwrites the roster at <paramref name="id"/> — the Regenerate path, where
    /// someone reopens a saved list, edits the numbers and saves it back under the same
    /// link.
    ///
    /// Merge rather than Replace, and <see cref="CreatedColumn"/> is deliberately absent
    /// from the payload: a merge only touches the properties it carries, so the original
    /// creation time survives an edit instead of being reset to now on every save.
    /// </summary>
    public async Task UpdateAsync(string id, string groupName, string rawText, IReadOnlyList<string> numbers, CancellationToken cancellationToken = default)
    {
        if (!ShortId.IsValid(id)) throw new ArgumentException("Not a roster id.", nameof(id));
        Validate(groupName, rawText, numbers);

        var table = _tables.GetTableClient(TableName);
        var entity = new TableEntity(PartitionKey, id)
        {
            [GroupNameColumn] = groupName,
            [RawTextColumn] = rawText,
            [NumbersColumn] = string.Join(NumbersSeparator, numbers),
            [UpdatedColumn] = DateTimeOffset.UtcNow
        };

        try
        {
            await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken);
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await table.CreateIfNotExistsAsync(cancellationToken);
            await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken);
        }
    }

    /// <summary>
    /// Reads a roster, or null if there is no such id.
    ///
    /// A missing table reads as a missing roster rather than an error: on a fresh
    /// Azurite volume the table genuinely does not exist until the first write, and a
    /// first-time visitor following a stale link should get the 404 page, not a 500.
    /// </summary>
    public async Task<SmsGroup?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        // Checked here rather than trusted from the route: this value becomes a RowKey,
        // and RowKeys reject '/', '\', '#', '?' and control characters with a 400 — so a
        // hand-edited URL would otherwise surface as an unhandled exception.
        if (!ShortId.TryNormalise(id, out var key)) return null;

        var table = _tables.GetTableClient(TableName);

        try
        {
            var entity = await table.GetEntityAsync<TableEntity>(PartitionKey, key, cancellationToken: cancellationToken);
            return ToGroup(key, entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static SmsGroup ToGroup(string id, TableEntity entity) => new()
    {
        Id = id,
        GroupName = entity.GetString(GroupNameColumn) ?? string.Empty,
        RawText = entity.GetString(RawTextColumn) ?? string.Empty,
        Numbers = (entity.GetString(NumbersColumn) ?? string.Empty)
            .Split(NumbersSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        CreatedAt = entity.GetDateTimeOffset(CreatedColumn),
        UpdatedAt = entity.GetDateTimeOffset(UpdatedColumn)
    };

    /// <summary>
    /// Inserts, creating the table first only if the insert says it is missing.
    ///
    /// The obvious alternative — <c>CreateIfNotExistsAsync</c> before every write — costs
    /// a round trip on every save forever to handle a condition that is true once in the
    /// lifetime of a storage account. This pays that cost only on the write that
    /// actually hits it.
    /// </summary>
    private static async Task AddWithTableCreateAsync(TableClient table, TableEntity entity, CancellationToken cancellationToken)
    {
        try
        {
            await table.AddEntityAsync(entity, cancellationToken);
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await table.CreateIfNotExistsAsync(cancellationToken);
            await table.AddEntityAsync(entity, cancellationToken);
        }
    }

    /// <summary>
    /// A 404 on a write means the table is gone, not the entity — but the error code is
    /// matched as well because a bare 404 from a write is worth being narrow about.
    /// </summary>
    private static bool IsTableMissing(RequestFailedException ex) =>
        ex.Status == 404 && (ex.ErrorCode is null || ex.ErrorCode == "TableNotFound");

    private static void Validate(string groupName, string rawText, IReadOnlyList<string> numbers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        if (groupName.Length > RosterLimits.MaxGroupNameLength)
            throw new ArgumentException($"Roster name is longer than {RosterLimits.MaxGroupNameLength} characters.", nameof(groupName));

        if (rawText.Length > RosterLimits.MaxRawTextLength)
            throw new ArgumentException($"Pasted text is longer than {RosterLimits.MaxRawTextLength} characters.", nameof(rawText));

        if (numbers.Count == 0)
            throw new ArgumentException("A roster needs at least one number.", nameof(numbers));

        if (numbers.Count > RosterLimits.MaxNumbers)
            throw new ArgumentException($"A roster holds at most {RosterLimits.MaxNumbers} numbers.", nameof(numbers));
    }
}
