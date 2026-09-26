using System.Reflection;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Messaging;
using Rahiq.Infrastructure.Common.Outbox;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Infrastructure.Common.Security;
using Rahiq.Infrastructure.Common.Storage;
using Rahiq.SharedKernel;

namespace Rahiq.Infrastructure.Common;

public static class DependencyInjection
{
    public static IServiceCollection AddRahiqInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        }

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        dataSourceBuilder.ConfigureJsonOptions(JsonDefaults.Options);
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        services.AddDbContext<RahiqDbContext>(options => options
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable("__ef_unused", "infra"))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IDbSession, DbSession>();
        services.AddScoped<IEventPublisher, EventPublisher>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<ISender, Sender>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentActor, CurrentActor>();
        services.TryAddSingleton<IClock, SystemClock>();

        services.Configure<StoreOptions>(configuration.GetSection(StoreOptions.Section));
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.Section));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));

        var storage = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        if (storage.Provider.Equals("s3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IBlobStorage, S3BlobStorage>();
        }
        else
        {
            services.AddSingleton<IBlobStorage, FileSystemBlobStorage>();
        }

        services.AddSingleton<SqlMigrator>();
        services.AddSingleton<OutboxProcessor>();
        services.AddHostedService<OutboxWorker>();

        return services;
    }

    /// <summary>Registers a module's handlers, event handlers, validators and event types.</summary>
    public static IServiceCollection AddModuleAssembly(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        EventTypeRegistry.Register(assembly);

        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsClass: true, IsGenericTypeDefinition: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(i => i.IsGenericType))
            {
                var definition = contract.GetGenericTypeDefinition();
                if (definition == typeof(IRequestHandler<,>) || definition == typeof(IEventHandler<>) || definition == typeof(IValidator<>))
                {
                    services.AddScoped(contract, type);
                }
            }

            if (typeof(IModelContributor).IsAssignableFrom(type))
            {
                services.AddSingleton(typeof(IModelContributor), type);
            }
        }

        return services;
    }

    /// <summary>Event types defined in a Contracts assembly (so the outbox can read them back).</summary>
    public static IServiceCollection AddEventTypes(this IServiceCollection services, Assembly assembly)
    {
        EventTypeRegistry.Register(assembly);
        return services;
    }
}
