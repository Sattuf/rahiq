using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Modules.Customers.Contracts;
using Rahiq.Modules.Customers.Infrastructure;

namespace Rahiq.Modules.Customers;

public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(CustomersModule).Assembly);
        services.AddEventTypes(typeof(CustomerDataErased).Assembly);
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.AddHttpClient("hibp", c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddScoped<TokenService>();
        services.AddScoped<PasswordPolicy>();
        services.AddScoped<ICustomerDirectory, CustomerDirectory>();
        return services;
    }
}
