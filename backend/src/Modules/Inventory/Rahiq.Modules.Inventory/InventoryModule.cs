using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Infrastructure.Common.Outbox;
using Rahiq.Modules.Inventory.Application;
using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Inventory.Infrastructure;

namespace Rahiq.Modules.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(InventoryModule).Assembly);
        services.AddEventTypes(typeof(ReservationsExpired).Assembly);
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddRecurringCommand<ReleaseExpiredReservationsCommand>(TimeSpan.FromMinutes(1));
        services.AddRecurringCommand<RaiseInventoryAlertsCommand>(TimeSpan.FromHours(24));
        return services;
    }
}
