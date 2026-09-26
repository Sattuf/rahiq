using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Infrastructure.Common.Outbox;
using Rahiq.Modules.Payments.Application;
using Rahiq.Modules.Payments.Contracts;
using Rahiq.Modules.Payments.Infrastructure;

namespace Rahiq.Modules.Payments;

public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(PaymentsModule).Assembly);
        services.AddEventTypes(typeof(PaymentSucceeded).Assembly);
        services.Configure<PaymentOptions>(configuration.GetSection(PaymentOptions.Section));
        services.AddHttpClient("sandbox-webhook", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient("iyzico", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<SandboxPaymentProvider>();
        services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<SandboxPaymentProvider>());
        services.AddScoped<IPaymentProvider, IyzicoPaymentProvider>();
        services.AddScoped<IPaymentGateway, PaymentGateway>();
        services.AddRecurringCommand<PollPendingPaymentsCommand>(TimeSpan.FromMinutes(5));
        services.AddRecurringCommand<ReconcilePaymentsCommand>(TimeSpan.FromHours(1));
        return services;
    }
}
