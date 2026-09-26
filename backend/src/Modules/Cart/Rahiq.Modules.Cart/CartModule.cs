using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Modules.Cart.Contracts;
using Rahiq.Modules.Cart.Infrastructure;

namespace Rahiq.Modules.Cart;

public static class CartModule
{
    public static IServiceCollection AddCartModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(CartModule).Assembly);
        services.AddScoped<CartStore>();
        services.AddScoped<ICartAccess, CartAccess>();
        return services;
    }
}
