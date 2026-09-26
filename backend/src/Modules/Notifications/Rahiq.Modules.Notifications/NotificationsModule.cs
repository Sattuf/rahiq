using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Modules.Customers.Contracts;
using Rahiq.Modules.Notifications.Application;
using Rahiq.Modules.Notifications.Contracts;
using Rahiq.Modules.Notifications.Infrastructure;

namespace Rahiq.Modules.Notifications;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(NotificationsModule).Assembly);
        services.AddEventTypes(typeof(StaffAlertRaised).Assembly);
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));
        var transport = configuration.GetSection(EmailOptions.Section).Get<EmailOptions>()?.Transport ?? "smtp";
        if (transport == "log")
        {
            services.AddScoped<IEmailTransport, LogOnlyEmailTransport>();
        }
        else
        {
            services.AddScoped<IEmailTransport, SmtpEmailTransport>();
        }

        services.AddScoped<Mailer>();
        services.AddScoped<IOtpDelivery, NotificationHandlers>();
        return services;
    }
}
