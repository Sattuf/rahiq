using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Customers.Contracts;
using Rahiq.Modules.Customers.Domain;
using Rahiq.Modules.Customers.Infrastructure;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Customers.Application;

public sealed record AddressDto(Guid Id, string? Label, AddressData Data, bool IsDefault);

public sealed record GetProfileQuery(Guid CustomerId) : IQuery<Result<CustomerProfileDto>>;

public sealed record UpdateProfileCommand(Guid CustomerId, string? Name, string? Phone, string Locale) : ICommand<Result>;

/// <summary>Marketing consent is separate and explicit (KVKK), recorded append-only with its source for İYS.</summary>
public sealed record SetConsentCommand(Guid CustomerId, string Channel, bool Granted) : ICommand<Result>;

public sealed record ListAddressesQuery(Guid CustomerId) : IQuery<IReadOnlyList<AddressDto>>;

public sealed record SaveAddressCommand(Guid CustomerId, Guid? AddressId, string? Label, AddressData Data, bool IsDefault) : ICommand<Result<Guid>>;

public sealed record DeleteAddressCommand(Guid CustomerId, Guid AddressId) : ICommand<Result>;

public sealed record ExportMyDataQuery(Guid CustomerId) : IQuery<Result<JsonElement>>;

/// <summary>
/// KVKK erasure (security.md §9): personal data goes; orders stay for the legal accounting period, as the law requires.
/// </summary>
public sealed record EraseMyAccountCommand(Guid CustomerId) : ICommand<Result>;

internal sealed class AccountHandlers(
    RahiqDbContext db,
    IDbSession session,
    ICustomerDirectory directory,
    IOrderReader orders,
    IEventPublisher events,
    IAuditLog audit,
    IClock clock)
    : IRequestHandler<GetProfileQuery, Result<CustomerProfileDto>>,
      IRequestHandler<UpdateProfileCommand, Result>,
      IRequestHandler<SetConsentCommand, Result>,
      IRequestHandler<ListAddressesQuery, IReadOnlyList<AddressDto>>,
      IRequestHandler<SaveAddressCommand, Result<Guid>>,
      IRequestHandler<DeleteAddressCommand, Result>,
      IRequestHandler<ExportMyDataQuery, Result<JsonElement>>,
      IRequestHandler<EraseMyAccountCommand, Result>
{
    private static readonly Error NotFound = Error.NotFound("customer.not_found", "Account not found.");

    public async Task<Result<CustomerProfileDto>> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        var customer = await Find(request.CustomerId, cancellationToken);
        return customer is null ? NotFound : await ProfileOf(customer, cancellationToken);
    }

    public async Task<Result> Handle(UpdateProfileCommand request, CancellationToken cancellationToken)
    {
        var customer = await Find(request.CustomerId, cancellationToken);
        if (customer is null)
        {
            return NotFound;
        }

        customer.UpdateProfile(request.Name, request.Phone, request.Locale);
        return Result.Success();
    }

    public async Task<Result> Handle(SetConsentCommand request, CancellationToken cancellationToken)
    {
        if (request.Channel is not ("email" or "sms"))
        {
            return Error.Validation("consent.channel_invalid", "Channel is email or sms.");
        }

        var customer = await Find(request.CustomerId, cancellationToken);
        if (customer is null)
        {
            return NotFound;
        }

        await directory.RecordMarketingConsentAsync(customer.Email, customer.Id, request.Channel, request.Granted, "account", cancellationToken);
        return Result.Success();
    }

    public async Task<IReadOnlyList<AddressDto>> Handle(ListAddressesQuery request, CancellationToken cancellationToken) =>
        [.. (await db.Set<Address>().AsNoTracking().Where(a => a.CustomerId == request.CustomerId).OrderByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt).ToListAsync(cancellationToken))
            .Select(a => new AddressDto(a.Id, a.Label, JsonSerializer.Deserialize<AddressData>(a.Data, JsonDefaults.Options)!, a.IsDefault))];

    public async Task<Result<Guid>> Handle(SaveAddressCommand request, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(request.Data, JsonDefaults.Options);
        if (request.IsDefault)
        {
            await db.Set<Address>().Where(a => a.CustomerId == request.CustomerId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false), cancellationToken);
        }

        if (request.AddressId is { } id)
        {
            var address = await db.Set<Address>().FirstOrDefaultAsync(a => a.Id == id && a.CustomerId == request.CustomerId, cancellationToken);
            if (address is null)
            {
                return Error.NotFound("address.not_found", "Address not found.");
            }

            address.Label = request.Label;
            address.Data = data;
            address.IsDefault = request.IsDefault;
            return id;
        }

        var created = new Address { Id = Ids.New(), CustomerId = request.CustomerId, Label = request.Label, Data = data, IsDefault = request.IsDefault, CreatedAt = clock.UtcNow };
        db.Add(created);
        return created.Id;
    }

    public async Task<Result> Handle(DeleteAddressCommand request, CancellationToken cancellationToken)
    {
        var deleted = await db.Set<Address>().Where(a => a.Id == request.AddressId && a.CustomerId == request.CustomerId).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? Error.NotFound("address.not_found", "Address not found.") : Result.Success();
    }

    public async Task<Result<JsonElement>> Handle(ExportMyDataQuery request, CancellationToken cancellationToken)
    {
        var customer = await Find(request.CustomerId, cancellationToken);
        if (customer is null)
        {
            return NotFound;
        }

        await session.EnsureOpenAsync(cancellationToken);
        var consents = await session.Connection.QueryAsync<(string Channel, bool Granted, string Source, DateTime At)>(new CommandDefinition(
            "SELECT channel, granted, source, at FROM customers.consents WHERE email = @email ORDER BY at",
            new { email = customer.Email }, session.Transaction, cancellationToken: cancellationToken));
        var export = new
        {
            exportedAt = clock.UtcNow,
            profile = new { customer.Email, customer.Name, customer.Phone, customer.Locale, customer.CreatedAt },
            addresses = await Handle(new ListAddressesQuery(customer.Id), cancellationToken),
            consents = consents.Select(c => new { c.Channel, c.Granted, c.Source, c.At }),
            orders = await orders.ForCustomerAsync(customer.Id, cancellationToken),
        };
        return JsonSerializer.SerializeToElement(export, JsonDefaults.Options);
    }

    public async Task<Result> Handle(EraseMyAccountCommand request, CancellationToken cancellationToken)
    {
        var customer = await Find(request.CustomerId, cancellationToken);
        if (customer is null)
        {
            return NotFound;
        }

        var email = customer.Email;
        await directory.RecordMarketingConsentAsync(email, customer.Id, "email", false, "account_erased", cancellationToken);
        await directory.RecordMarketingConsentAsync(email, customer.Id, "sms", false, "account_erased", cancellationToken);
        await db.Set<Address>().Where(a => a.CustomerId == customer.Id).ExecuteDeleteAsync(cancellationToken);
        await db.Set<RefreshToken>().Where(t => t.SubjectId == customer.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.UtcNow), cancellationToken);
        customer.Erase(clock.UtcNow);
        events.Publish(new CustomerDataErased(customer.Id, email));
        audit.Record("customer.erased", "customer", customer.Id.ToString());
        return Result.Success();
    }

    private Task<Customer?> Find(Guid id, CancellationToken cancellationToken) =>
        db.Set<Customer>().FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, cancellationToken);

    private async Task<CustomerProfileDto> ProfileOf(Customer customer, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var consents = (await session.Connection.QueryAsync<(string Channel, bool Granted)>(new CommandDefinition("""
            SELECT DISTINCT ON (channel) channel, granted FROM customers.consents WHERE email = @email AND purpose = 'marketing' ORDER BY channel, at DESC, id DESC
            """, new { email = customer.Email }, session.Transaction, cancellationToken: cancellationToken))).ToDictionary(c => c.Channel, c => c.Granted);
        return new CustomerProfileDto(customer.Id, customer.Email, customer.Name, customer.Phone, customer.Locale,
            consents.GetValueOrDefault("email"), consents.GetValueOrDefault("sms"));
    }
}
