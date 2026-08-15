using System.Threading.Channels;
using Smser.Library;

namespace Smser.Web.Services;

/// <summary>
/// Takes visit entries off the request thread.
///
/// A page view must not wait on a storage write, and must not fail because of one. So
/// <see cref="Record"/> only drops the entry into a bounded queue and returns; the write
/// happens on <see cref="VisitWriter"/>'s own loop. The queue is bounded and drops the
/// oldest entry when it is full, which is the right trade for an analytics log: losing a
/// line under a burst is fine, growing without limit until the app falls over is not.
/// </summary>
public sealed class VisitRecorder
{
    private const int Capacity = 2_000;

    private readonly Channel<VisitEntry> _queue = Channel.CreateBounded<VisitEntry>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private readonly ILogger<VisitRecorder> _logger;
    private int _dropped;

    public VisitRecorder(ILogger<VisitRecorder> logger) => _logger = logger;

    public ChannelReader<VisitEntry> Reader => _queue.Reader;

    public void Record(VisitEntry entry)
    {
        if (_queue.Writer.TryWrite(entry)) return;

        // Only reachable if the channel is completed, since DropOldest never refuses a
        // write. Counted rather than logged per occurrence so a burst cannot turn a
        // dropped analytics line into a flood of log noise.
        if (Interlocked.Increment(ref _dropped) % 100 == 1)
        {
            _logger.LogWarning("Visit log queue rejected {Dropped} entries", _dropped);
        }
    }

    /// <summary>Builds an entry from the request. Returns null for things not worth logging.</summary>
    public static VisitEntry? Describe(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";

        // Static assets, the health probe and the version endpoint. App Service polls
        // /alive continuously, so logging it would bury real visits in noise and bill for
        // the privilege.
        if (path is "/alive" or "/version" or "/health") return null;
        if (Path.HasExtension(path)) return null;

        // Only GETs. A POST is followed by a redirect to a GET, so logging both would
        // double-count every save, and the interesting POST outcomes are recorded
        // explicitly as roster-created and roster-updated.
        if (!HttpMethods.IsGet(request.Method)) return null;

        var rosterId = RosterIdFrom(path);

        return new VisitEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Event = rosterId is null ? VisitEvents.Page : VisitEvents.RosterViewed,
            Path = path,
            RosterId = rosterId,
            Ip = ClientIp(context),
            UserAgent = request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            Referer = request.Headers.Referer.ToString() is { Length: > 0 } referer ? referer : null,
            Country = Header(request, "CF-IPCountry") ?? Header(request, "X-Country-Code")
        };
    }

    /// <summary>
    /// The caller's address. Correct behind App Service only because ForwardedHeaders is
    /// enabled and configured in Program.cs — without that this is the reverse proxy's
    /// address and every visitor looks like the same person.
    /// </summary>
    public static string? ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    /// <summary>The roster id in <c>/new/{id}</c>, or null for any other path.</summary>
    private static string? RosterIdFrom(string path)
    {
        const string prefix = "/new/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var candidate = path[prefix.Length..].TrimEnd('/');

        return ShortId.TryNormalise(candidate, out var id) ? id : null;
    }

    private static string? Header(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var value) && value.ToString() is { Length: > 0 } text
            ? text
            : null;
}

/// <summary>
/// Drains the queue and writes to storage, in batches, forever.
///
/// Batching matters: Table Storage bills per transaction, and a busy minute is otherwise
/// a few hundred separate round trips for data nobody reads in real time. A short linger
/// lets entries accumulate without making the log meaningfully stale.
/// </summary>
public sealed class VisitWriter : BackgroundService
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(5);
    private const int MaxBatch = 100;

    private readonly VisitRecorder _recorder;
    private readonly VisitLog _log;
    private readonly ILogger<VisitWriter> _logger;

    public VisitWriter(VisitRecorder recorder, VisitLog log, ILogger<VisitWriter> logger)
    {
        _recorder = recorder;
        _log = log;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<VisitEntry>(MaxBatch);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _recorder.Reader.WaitToReadAsync(stoppingToken)) break;

                // One entry has arrived; wait a moment for its neighbours rather than
                // paying a round trip for each.
                using var linger = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                linger.CancelAfter(Linger);

                batch.Clear();
                try
                {
                    while (batch.Count < MaxBatch && await _recorder.Reader.WaitToReadAsync(linger.Token))
                    {
                        while (batch.Count < MaxBatch && _recorder.Reader.TryRead(out var entry))
                        {
                            batch.Add(entry);
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The linger expired. That is the normal way out of the loop above.
                }

                if (batch.Count > 0) await _log.RecordAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Analytics must never take the site down. Log it and keep serving; the
                // entries in this batch are gone, which is an acceptable loss.
                _logger.LogError(ex, "Failed to write {Count} visit log entries", batch.Count);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

public static class VisitLoggingExtensions
{
    /// <summary>
    /// Records a line for every page view. Placed after the rate limiter so blocked
    /// floods are not logged, and after ForwardedHeaders so the address is the visitor's.
    /// </summary>
    public static IApplicationBuilder UseVisitLogging(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var recorder = context.RequestServices.GetRequiredService<VisitRecorder>();
            var entry = VisitRecorder.Describe(context);

            if (entry is not null) recorder.Record(entry);

            await next();
        });
    }
}
