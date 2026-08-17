using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Smser.Library;
using Smser.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Suppress "Server: Kestrel". This has to be done on Kestrel itself — Kestrel writes the
// header below the middleware layer, so removing it in a response callback looks correct
// and does nothing.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.AddServiceDefaults();

builder.Services.AddRazorPages(options =>
{
    // /new is the only page that writes to storage. Rate limiting in ASP.NET Core is
    // endpoint metadata, and a Razor Page is one endpoint covering both its GET and its
    // POST — so this necessarily limits viewing a saved roster at the same rate as
    // saving one. That is why the limit is set where it is: see the "save" policy.
    options.Conventions.AddPageApplicationModelConvention("/New",
        model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute("save")));
});

// Lowercase generated URLs. Note the constraint this puts on ids: LowercaseUrls also
// lowercases route parameter *values*, which is why ShortId's alphabet has no uppercase
// in it — see the comment there.
builder.Services.AddRouting(o => o.LowercaseUrls = true);

// The Table Storage client. Reads ConnectionStrings:tables, which the Aspire AppHost
// injects when running under it and appsettings.Development.json supplies
// (UseDevelopmentStorage=true) when this project is run on its own against Azurite.
builder.AddAzureTableServiceClient("tables");

builder.Services.AddSingleton<SmsGroupStore>();
builder.Services.AddSingleton<QrCodeGenerator>();

// Visit auditing. The recorder is a queue the request thread drops entries into; the
// writer drains it on its own loop, so a page view never waits on a storage write and
// never fails because of one.
builder.Services.AddSingleton<VisitLog>();
builder.Services.AddSingleton<VisitRecorder>();
builder.Services.AddHostedService<VisitWriter>();

// Bot checks on the roster form. TimeProvider is registered rather than read from
// TimeProvider.System inside FormGuard so the elapsed-time rule can be tested without
// tests that sleep.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FormGuard>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Saving a roster is anonymous and writes to table storage, with no captcha. Nothing
    // is exposed by abusing it, but it is the only endpoint here that bills per
    // transaction, and it is the one that mints ids.
    //
    // One policy, three buckets. Rate limiting is endpoint metadata and a Razor Page is a
    // single endpoint covering its GET and its POST, so a single number here would have to
    // be loose enough for the loosest thing the page does — which is why this used to sit
    // at twenty for everything. Partitioning inside the policy is what lets the write be
    // held to a few a minute without throttling people reading a roster.
    //
    // Separate keys mean separate budgets: reading a roster never spends the allowance for
    // saving one, and vice versa.
    options.AddPolicy("save", context =>
    {
        var caller = CallerKey(context);

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            // Reads. Opening a roster, following a share link, reloading after a save.
            // Costs a storage read and nothing else.
            return PerCaller($"read:{caller}", permitLimit: 60);
        }

        // Import parses the pasted text and returns the same page. It touches no storage
        // and mints no id, so it does not need the write budget — but it is a POST that
        // does real parsing work, so it is not free either. It also fires automatically
        // once per photo, which is why it is not held to the save limit: reading two pages
        // of a roster is two imports in quick succession by design.
        if (IsImport(context)) return PerCaller($"import:{caller}", permitLimit: 20);

        // The write. This is the one that mints an id, bills a transaction, and is worth
        // anything at all to a bot.
        //
        // These were all half what they are now, and the save was the one that bit: two a
        // minute assumed you save a roster and correct it once, which is not what editing
        // a photographed roster actually looks like. The OCR gets names wrong, you fix a
        // number, save, spot another, and the third correction inside a minute is refused
        // — the app throttling the exact workflow it was built for. Doubling keeps bulk
        // creation pointless while leaving room to iterate.
        return PerCaller($"save:{caller}", permitLimit: 4);
    });

    // Throttling is invisible otherwise. UseVisitLogging sits behind the limiter
    // deliberately — so a flood is not also a logged flood — which means a rejected
    // caller leaves no trace at all unless it is recorded here, and "are we being
    // hammered" is exactly the question the audit log exists to answer.
    //
    // Retry-After is set because a 429 without one tells a well-behaved client nothing
    // about when to come back, and the fixed window makes the answer knowable.
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";

        var recorder = context.HttpContext.RequestServices.GetRequiredService<VisitRecorder>();
        recorder.Record(new VisitEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Event = VisitEvents.Throttled,
            Path = context.HttpContext.Request.Path.Value ?? "/",
            Ip = VisitRecorder.ClientIp(context.HttpContext),
            UserAgent = context.HttpContext.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            Referer = context.HttpContext.Request.Headers.Referer.ToString() is { Length: > 0 } r ? r : null
        });

        return ValueTask.CompletedTask;
    };

    // Rosters are protected by the unguessability of their ids and nothing else, so the
    // global limiter is also what makes scanning for them impractical rather than merely
    // slow. See ShortId for the arithmetic this is the other half of.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(CallerKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 240,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

// Configures the ForwardedHeaders middleware the host adds from
// ASPNETCORE_FORWARDEDHEADERS_ENABLED — it does not add a second one. Without this the
// rate limiter partitions on the reverse proxy's address once this is behind App Service,
// which makes every visitor share one bucket.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // KnownIPNetworks, not the obsolete KnownNetworks (ASPDEPR005).
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    // One hop. Anything more and a caller can spoof the address they are limited on by
    // sending their own X-Forwarded-For.
    options.ForwardLimit = 1;
});

// A year rather than the framework's 30 days. includeSubDomains stays off until the
// hostname's subdomains are known to be HTTPS-only — turning it on is not reversible
// within the max-age.
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = false;
    options.Preload = false;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

// Renders the app's own page for status codes with no body of their own — every
// /new/{id} that does not resolve, and every request the rate limiter turns away.
//
// The status code is passed through as a query string because this catches both. Without
// it a throttled visitor is told their list does not exist, which is a lie, and a
// confusing one to act on: the fix for "slow down" is to wait, and the fix for "no such
// list" is to check the link. That was survivable while the limit was twenty a minute and
// nobody reached it; at two saves a minute it is a page real people will see.
//
// The browser's address bar keeps the original URL — re-execution is internal — so this
// query string is never visible to anyone.
app.UseStatusCodePagesWithReExecute("/not-found", "?code={0}");

// Before everything else, so the headers reach 404s and error responses too.
app.UseSmserSecurityHeaders();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

// After the rate limiter, so a blocked flood is not also a logged one, and after
// forwarded headers, so the address recorded is the visitor's rather than the proxy's.
app.UseVisitLogging();
app.MapRazorPages();

// /alive for the platform health probe and /version for deploy smoke tests.
app.MapDefaultEndpoints();

app.Run();

static string CallerKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

/// <summary>True for the Import button, which posts to the same page under a handler.</summary>
static bool IsImport(HttpContext context) =>
    context.Request.Query.TryGetValue("handler", out var handler) &&
    string.Equals(handler, "Import", StringComparison.OrdinalIgnoreCase);

static RateLimitPartition<string> PerCaller(string key, int permitLimit) =>
    RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0
    });

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this app in tests. Top-level
/// statements generate an internal Program class, which the factory cannot reach — and it
/// has to be declared after every top-level statement in the file, local functions included.
/// </summary>
public partial class Program;
