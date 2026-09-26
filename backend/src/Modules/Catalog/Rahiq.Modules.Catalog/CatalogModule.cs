using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Catalog.Infrastructure;

namespace Rahiq.Modules.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(CatalogModule).Assembly);
        services.AddEventTypes(typeof(ProductPublished).Assembly);
        services.Configure<SearchOptions>(configuration.GetSection(SearchOptions.Section));
        services.AddScoped<ICatalogReader, CatalogReader>();
        services.AddScoped<ProductSearch>();
        services.AddScoped<ICatalogSearch, CatalogSearch>();
        return services;
    }
}
