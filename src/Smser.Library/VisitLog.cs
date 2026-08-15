using Azure;
using Azure.Data.Tables;

namespace Smser.Library;

/// <summary>What happened. Kept as short strings so a table query reads plainly.</summary>
public static class VisitEvents
{
    /// <summary>A page view that is not a roster — the home page, /new, an error page.</summary>
    public const string Page = "page";

    /// <summary>Somebody opened a saved roster at /new/{id}.</summary>
    public const string RosterViewed = "roster-viewed";

    /// <summary>A roster was saved for the first time.</summary>
    public const string RosterCreated = "roster-created";

    /// <summary>An existing roster was edited and saved again.</summary>
    public const string RosterUpdated = "roster-updated";
}

/// <summary>One line in the audit log.</summary>
public sealed record VisitEntry
{
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>One of <see cref="VisitEvents"/>.</summary>
    public required string Event { get; init; }

    public required string Path { get; init; }

    /// <summary>The roster this concerns, when the path names one.</summary>
    public string? RosterId { get; init; }

    /// <summary>
    /// Caller address. Behind App Service this is the real client only because
    /// ForwardedHeaders is enabled and configured — see Program.cs. Null when the address
    /// is unavailable, which happens in tests and for some proxy configurations.
    /// </summary>
    public string? Ip { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Where they came from, when the browser says.</summary>
    public string? Referer { get; init; }

    /// <summary>Two-letter country, when the front end supplies one. Null elsewhere.</summary>
    public string? Country { get; init; }

    /// <summary>Numbers on the roster, for the create and update events. Null otherwise.</summary>
    public int? NumberCount { get; init; }
}

/// <summary>
/// Append-only visit log in Azure Table Storage.
///
/// Partitioned by UTC date, so "what happened today" and "everything in March" are
/// partition scans rather than table scans, and a retention sweep deletes whole
/// partitions instead of hunting rows. Row keys are descending ticks, which makes the
/// natural table order newest-first within a day — Table Storage sorts by RowKey
/// ascending and offers no other ordering.
///
/// This holds IP addresses, which are personal data in most of the world. Nothing else
/// in this app stores anything about who is visiting, so this is the one place that
/// needs a retention answer; see <see cref="DeleteBeforeAsync"/>.
/// </summary>
public sealed class VisitLog
{
    private const string TableName = "visits";

    /// <summary>Long enough for any real browser, short of the 32K property cap.</summary>
    private const int MaxTextLength = 512;

    private readonly TableServiceClient _tables;

    public VisitLog(TableServiceClient tables) => _tables = tables;

    public async Task RecordAsync(VisitEntry entry, CancellationToken cancellationToken = default)
    {
        var table = _tables.GetTableClient(TableName);
        var tableEntity = ToEntity(entry);

        try
        {
            await table.AddEntityAsync(tableEntity, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            await table.CreateIfNotExistsAsync(cancellationToken);
            await table.AddEntityAsync(tableEntity, cancellationToken);
        }
    }

    /// <summary>
    /// Writes a batch into one partition per call. Table Storage transactions are limited
    /// to a single partition and 100 entities, so the caller's batch is split on both.
    /// </summary>
    public async Task RecordAsync(IReadOnlyList<VisitEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return;

        var table = _tables.GetTableClient(TableName);

        foreach (var day in entries.GroupBy(e => PartitionFor(e.OccurredAt)))
        {
            foreach (var chunk in day.Chunk(100))
            {
                var actions = chunk
                    .Select(e => new TableTransactionAction(TableTransactionActionType.Add, ToEntity(e)))
                    .ToList();

                try
                {
                    await table.SubmitTransactionAsync(actions, cancellationToken);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    await table.CreateIfNotExistsAsync(cancellationToken);
                    await table.SubmitTransactionAsync(actions, cancellationToken);
                }
            }
        }
    }

    /// <summary>Reads a day, newest first.</summary>
    public async Task<IReadOnlyList<VisitEntry>> ReadDayAsync(DateOnly day, int take = 200, CancellationToken cancellationToken = default)
    {
        var table = _tables.GetTableClient(TableName);
        var partition = day.ToString("yyyy-MM-dd");
        var results = new List<VisitEntry>();

        try
        {
            await foreach (var entity in table.QueryAsync<TableEntity>(
                e => e.PartitionKey == partition, maxPerPage: take, cancellationToken: cancellationToken))
            {
                results.Add(FromEntity(entity));
                if (results.Count >= take) break;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return [];
        }

        return results;
    }

    /// <summary>
    /// Deletes every entry older than <paramref name="cutoff"/>, and reports how many.
    ///
    /// This exists because the log holds IP addresses and nothing should hold those
    /// forever. It is not called on a schedule yet — there is no worker to call it from —
    /// so for now it is the thing a person or a cron runs.
    /// </summary>
    public async Task<int> DeleteBeforeAsync(DateOnly cutoff, CancellationToken cancellationToken = default)
    {
        var table = _tables.GetTableClient(TableName);
        var boundary = cutoff.ToString("yyyy-MM-dd");
        var deleted = 0;

        try
        {
            // PartitionKey is the date, so a string comparison is a date comparison.
            await foreach (var entity in table.QueryAsync<TableEntity>(
                e => string.Compare(e.PartitionKey, boundary) < 0, cancellationToken: cancellationToken))
            {
                await table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: cancellationToken);
                deleted++;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return 0;
        }

        return deleted;
    }

    private static string PartitionFor(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd");

    private static TableEntity ToEntity(VisitEntry entry)
    {
        // Descending ticks: Table Storage only sorts RowKey ascending, so storing the
        // complement is what makes a plain read return newest-first.
        var descending = (DateTimeOffset.MaxValue.Ticks - entry.OccurredAt.UtcTicks).ToString("D19");

        return new TableEntity(PartitionFor(entry.OccurredAt), $"{descending}-{ShortId.Create()}")
        {
            ["OccurredAt"] = entry.OccurredAt,
            ["Event"] = entry.Event,
            ["Path"] = Trim(entry.Path),
            ["RosterId"] = entry.RosterId,
            ["Ip"] = entry.Ip,
            ["UserAgent"] = Trim(entry.UserAgent),
            ["Referer"] = Trim(entry.Referer),
            ["Country"] = entry.Country,
            ["NumberCount"] = entry.NumberCount
        };
    }

    private static VisitEntry FromEntity(TableEntity entity) => new()
    {
        OccurredAt = entity.GetDateTimeOffset("OccurredAt") ?? entity.Timestamp ?? default,
        Event = entity.GetString("Event") ?? VisitEvents.Page,
        Path = entity.GetString("Path") ?? string.Empty,
        RosterId = entity.GetString("RosterId"),
        Ip = entity.GetString("Ip"),
        UserAgent = entity.GetString("UserAgent"),
        Referer = entity.GetString("Referer"),
        Country = entity.GetString("Country"),
        NumberCount = entity.GetInt32("NumberCount")
    };

    private static string? Trim(string? value) =>
        value is { Length: > MaxTextLength } ? value[..MaxTextLength] : value;
}
