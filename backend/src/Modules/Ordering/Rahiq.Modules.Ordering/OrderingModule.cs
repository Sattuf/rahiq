using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Modules.Ordering.Application;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Ordering.Infrastructure;

namespace Rahiq.Modules.Ordering;

public static class OrderingModule
{
    public static IServiceCollection AddOrderingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(OrderingModule).Assembly);
        services.AddEventTypes(typeof(OrderPlaced).Assembly);
        services.AddScoped<OrderNumbers>();
        services.AddScoped<IOrderReader, OrderReader>();
        services.AddScoped<CheckoutPayment>();
        return services;
    }
}
