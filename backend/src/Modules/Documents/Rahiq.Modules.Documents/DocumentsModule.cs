using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Infrastructure.Common;
using Rahiq.Infrastructure.Common.Outbox;
using Rahiq.Modules.Documents.Application;
using Rahiq.Modules.Documents.Contracts;
using Rahiq.Modules.Documents.Infrastructure;

namespace Rahiq.Modules.Documents;

public static class DocumentsModule
{
    public static IServiceCollection AddDocumentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleAssembly(typeof(DocumentsModule).Assembly);
        services.AddEventTypes(typeof(InvoiceIssued).Assembly);
        services.Configure<SellerOptions>(configuration.GetSection(SellerOptions.Section));
        services.AddScoped<IContractDocuments, ContractDocuments>();
        services.AddScoped<IEInvoiceProvider, SandboxEInvoiceProvider>();
        services.AddRecurringCommand<RetryInvoicesCommand>(TimeSpan.FromMinutes(5));
        return services;
    }
}
