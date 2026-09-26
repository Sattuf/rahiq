using Rahiq.SharedKernel;

namespace Rahiq.Modules.Customers.Contracts;

public interface ICustomerDirectory
{
    Task<Guid?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Times this customer refused a cash-on-delivery parcel. Two disables COD (commerce-flows.md §6).</summary>
    Task<int> CodRefusalsAsync(string email, CancellationToken cancellationToken);

    Task RecordCodRefusalAsync(string email, CancellationToken cancellationToken);

    /// <summary>Append-only consent record (İYS / KVKK).</summary>
    Task RecordMarketingConsentAsync(string email, Guid? customerId, string channel, bool granted, string source, CancellationToken cancellationToken);

    Task<bool> HasMarketingConsentAsync(string email, string channel, CancellationToken cancellationToken);
}

/// <summary>Sends the sign-in code. Implemented by Notifications; the code never enters the outbox.</summary>
public interface IOtpDelivery
{
    Task SendAsync(string email, string code, string locale, CancellationToken cancellationToken);
}

public sealed record CustomerDataErased(Guid CustomerId, string Email) : DomainEvent;
