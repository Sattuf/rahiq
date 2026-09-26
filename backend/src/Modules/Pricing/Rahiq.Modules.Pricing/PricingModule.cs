using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.Modules.Pricing.Infrastructure;

namespace Rahiq.Modules.Pricing;

public static class PricingModule
{
    public static IServiceCollection AddPricingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(PricingModule).Assembly);
        services.AddEventTypes(typeof(PriceChanged).Assembly);
        services.AddScoped<IPriceReader, PriceReader>();
        services.AddScoped<IQuoteService, QuoteService>();
        services.AddScoped<ICouponUsage, CouponUsage>();
        return services;
    }
}
