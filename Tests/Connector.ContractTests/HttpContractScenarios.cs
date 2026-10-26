using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.AniWorld.Api;
using Jellyfin.Plugin.AniWorld.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>Real loopback HTTP/MVC responses, not hand-written frontend JSON fixtures.</summary>
internal static class HttpContractScenarios
{
    internal static void RealJellyfinWireContract() => RunAsync(false).GetAwaiter().GetResult();

    internal static async Task RunAsync(bool serveBrowser)
    {
        using var fixture = new CatalogScenarios.Fixture { MissingBrowseTypes = true, VideoPlanning = serveBrowser, PartialVideoLanguages = serveBrowser };
        using var fileFixture = new MediaLifecycleScenarios.Fixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddSingleton(fixture.Grants);
        builder.Services.AddSingleton(fixture.Secrets);
        builder.Services.AddSingleton(fixture.Application);
        builder.Services.AddSingleton<UserRateLimiter>();
        builder.Services.AddSingleton(fileFixture.Routing);
        builder.Services.AddSingleton(new DownloadManagementService(fileFixture.Ownership, fixture.Client, fileFixture.Library));
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(options => options.AddPolicy(MediaBrowser.Common.Api.Policies.RequiresElevation, policy => policy.RequireRole("Administrator")));
        builder.Services.AddControllers().AddApplicationPart(typeof(AniWorldRequestsController).Assembly);
        builder.Services.Configure<MvcOptions>(options =>
        {
            options.OutputFormatters.RemoveType<SystemTextJsonOutputFormatter>();
            options.OutputFormatters.Add(new SystemTextJsonOutputFormatter(new JsonSerializerOptions(JsonDefaults.Options)));
        });

        await using var app = builder.Build();
        // Authenticated test identity only on this ephemeral loopback test host.
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "http-contract"), new Claim(ClaimTypes.Name, "Contract"), new Claim(ClaimTypes.Role, "Administrator")], "ContractTest"));
            await next();
        });
        app.UseAuthorization();
        app.MapControllers();
        if (serveBrowser)
        {
            app.MapGet("/", () => Results.Content("""
                <!doctype html><meta charset="utf-8"><title>Connector HTTP contract</title>
                <style>html,body{background:#181818;color:#eee;font:16px system-ui}</style>
                <div id="host"></div>
                <script type="module">
                window.ApiClient={getUrl:p=>'/'+p,fetch:async r=>{
                    const response=await fetch(r.url,{method:r.type,headers:r.headers,body:r.data});
                    const data=r.dataType==='text'?await response.text():await response.json();
                    if(!response.ok)throw new Error(data.error||String(response.status));return data;
                }};
                const html=await window.ApiClient.fetch({url:'/AniWorldRequests/Page',dataType:'text'});
                document.getElementById('host').innerHTML=html;
                const page=await import('/AniWorldRequests/PageScript');
                page.default(document.getElementById('AniWorldRequestsPage'),{});
                </script>
                """, "text/html"));
            app.MapPost("/__contract/fail-source", () => { fixture.FailVideoSearch = true; return Results.Ok(); });
            app.MapPost("/__contract/stop", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.Ok(); });
        }

        await app.StartAsync();
        var address = app.Urls.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        using (var discovery = JsonDocument.Parse(await http.GetStringAsync("/AniWorldRequests/Discover?mediaType=video&source=sto")))
        {
            var section = discovery.RootElement.GetProperty("sections")[0];
            if (section.GetProperty("source").GetString() != "sto" || section.GetProperty("items").GetArrayLength() != 1)
                throw new InvalidOperationException("Actual MVC discovery response does not match the frontend contract.");
        }
        using (var search = JsonDocument.Parse(await http.GetStringAsync("/AniWorldRequests/Search?query=gravity%20falls&mediaType=video&source=all")))
        {
            var groups = search.RootElement.GetProperty("groups");
            if (groups.GetArrayLength() != 3 || groups.EnumerateArray().Any(group => group.TryGetProperty("error", out _)))
                throw new InvalidOperationException("Actual MVC search response lost a source or produced an unexpected error.");
            if (groups.EnumerateArray().Any(group => group.GetProperty("data").GetProperty("results").GetArrayLength() == 0))
                throw new InvalidOperationException("Actual MVC search response lost valid source results.");
        }
        fixture.FailVideoSearch = true;
        using (var partial = JsonDocument.Parse(await http.GetStringAsync("/AniWorldRequests/Search?query=gravity%20falls&mediaType=video&source=all")))
        {
            var groups = partial.RootElement.GetProperty("groups").EnumerateArray().ToArray();
            if (!groups.Single(group => group.GetProperty("source").GetString() == "moflix").GetProperty("error").GetString()!.Contains("HTTP 500", StringComparison.Ordinal)
                || groups.Where(group => group.GetProperty("source").GetString() != "moflix").Any(group => group.TryGetProperty("error", out _)))
                throw new InvalidOperationException("Actual MVC response did not isolate the unavailable source.");
        }
        fixture.FailVideoSearch = false;
        if (serveBrowser)
        {
            Console.WriteLine("HTTP_CONTRACT_URL=" + address);
            Console.Out.Flush();
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await app.WaitForShutdownAsync(lifetime.Token);
        }
        await app.StopAsync();
    }
}
