using Anthropic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rahiq.Infrastructure.Common;
using Rahiq.Modules.Conversations.Application;
using Rahiq.Modules.Conversations.Application.Agent;
using Rahiq.Modules.Conversations.Contracts;
using Rahiq.Modules.Conversations.Infrastructure;
using Rahiq.Modules.Conversations.Infrastructure.Channels;
using Rahiq.Modules.Conversations.Presentation;

namespace Rahiq.Modules.Conversations;

public static class ConversationsModule
{
    public static IServiceCollection AddConversationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddModuleAssembly(typeof(ConversationsModule).Assembly);
        services.AddEventTypes(typeof(ConversationHandedOff).Assembly);

        services.Configure<ChannelOptions>(configuration.GetSection(ChannelOptions.Section));
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        services.PostConfigure<AgentOptions>(o =>
        {
            // The standard SDK variable works too, so the key can come from the platform's secret store unchanged.
            if (string.IsNullOrWhiteSpace(o.ApiKey) && configuration["ANTHROPIC_API_KEY"] is { Length: > 0 } key)
            {
                o.ApiKey = key;
            }
        });

        services.AddHttpClient(TelegramAdapter.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(MetaAdapter.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IChannelAdapter, TelegramAdapter>();
        services.AddSingleton<IChannelAdapter, MetaAdapter>();
        services.AddSingleton<IChannelAdapter, WhatsAppAdapter>();

        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return new AnthropicClient { ApiKey = o.ApiKey ?? string.Empty, Timeout = TimeSpan.FromSeconds(60), MaxRetries = 2 };
        });
        services.AddScoped<AgentTools>();
        services.AddScoped<AgentTurn>();

        // Production runs two API instances and a separate worker; Redis carries "conversation changed" between them,
        // so an update made anywhere reaches every open inbox. A single process (development, tests) needs no backplane.
        var signalR = services.AddSignalR();
        if (configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("rahiq-hub"));
        }
        services.AddSingleton<IConversationNotifier, SignalRConversationNotifier>();
        services.AddSingleton<ConversationWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ConversationWorker>());
        return services;
    }

    public static IEndpointRouteBuilder MapConversations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<ConversationsHub>(ConversationsHub.Path);
        return endpoints;
    }
}
