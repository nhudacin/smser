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

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Saving a roster is anonymous and writes to table storage, with no captcha. Nothing
    // is exposed by abusing it, but it is the only endpoint here that bills per
    // transaction, and it is the one that mints ids.
    //
    // Twenty a minute, not five, because this necessarily also covers GETs of the same
    // page — see the convention that attaches it. Twenty covers pasting a roster,
    // importing, spotting a wrong number, regenerating and reloading the result several
    // times over, and still costs an abuser two orders of magnitude more time than an
    // unlimited endpoint would.
    options.AddPolicy("save", PerCallerFixedWindow(permitLimit: 20));

    // Rosters are protected by the unguessability of their ids and nothing else, so the
    // global limiter is also what makes scanning for them impractical rather than merely
    // slow. See ShortId for the arithmetic this is the other half of.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(CallerKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
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

// Renders the app's own 404 page for status codes with no body of their own, which is
// every /new/{id} that does not resolve.
app.UseStatusCodePagesWithReExecute("/not-found");

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

static Func<HttpContext, RateLimitPartition<string>> PerCallerFixedWindow(int permitLimit) =>
    context => RateLimitPartition.GetFixedWindowLimiter(CallerKey(context), _ => new FixedWindowRateLimiterOptions
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
