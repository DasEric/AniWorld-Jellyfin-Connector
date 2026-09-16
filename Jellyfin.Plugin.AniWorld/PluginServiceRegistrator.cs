using Jellyfin.Plugin.AniWorld.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AniWorld;

/// <summary>Registers connector services with Jellyfin's DI container.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection
            .AddHttpClient<AniWorldClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(90);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-AniWorld-Requests/0.1.0");
            })
            .RedactLoggedHeaders(["X-Api-Key"])
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Never forward the custom API-key header through an upstream redirect.
                AllowAutoRedirect = false,
                UseCookies = false,
                // Matches the controller's hard source cap so one stalled
                // source cannot keep another allowed source waiting for a
                // connection slot until its own 15-second deadline expires.
                MaxConnectionsPerServer = 32,
            });
        serviceCollection.AddSingleton<RequestStore>();
        serviceCollection.AddSingleton<MediaAccessGrantStore>();
        serviceCollection.AddSingleton<JellixSelectionTokenStore>();
        serviceCollection.AddSingleton<UserRateLimiter>();
        serviceCollection.AddSingleton<JellyfinLibraryAvailabilityService>();
        serviceCollection.AddSingleton<AniWorldRequestApplicationService>();
        serviceCollection.AddSingleton<Jellyfin.Plugin.AniWorld.Integration.JellixBridge>();
        serviceCollection.AddSingleton(serviceProvider =>
            Plugin.Instance?.Secrets
            ?? throw new InvalidOperationException("AniWorld secret store is unavailable."));
    }
}
