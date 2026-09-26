using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Modules.Shipping.Contracts;
using Rahiq.Modules.Shipping.Infrastructure;

namespace Rahiq.Modules.Shipping;

public static class ShippingModule
{
    public static IServiceCollection AddShippingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(ShippingModule).Assembly);
        services.AddEventTypes(typeof(ShipmentHandedOver).Assembly);
        services.Configure<CarrierOptions>(configuration.GetSection(CarrierOptions.Section));
        services.AddScoped<IShippingRates, ShippingRates>();
        services.AddScoped<ICarrierGateway, SandboxCarrier>();
        return services;
    }
}
