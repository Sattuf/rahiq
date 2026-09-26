using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.Modules.Pricing.Domain;
using Rahiq.Modules.Pricing.Infrastructure;
using Rahiq.SharedKernel;
using Rahiq.SharedKernel.Compliance;

namespace Rahiq.Modules.Pricing.Application;

public sealed record SetPriceCommand(Guid VariantId, long Amount) : ICommand<Result>;

public sealed record PriceHistoryRow(long Amount, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);

public sealed record PriceHistoryQuery(Guid VariantId) : IQuery<IReadOnlyList<PriceHistoryRow>>;

public sealed record CouponInput(
    string Code, string Kind, long Value, long? MinSubtotal, string? Section, int? MaxUses, int? PerCustomerLimit,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Description, bool Active);

public sealed record CouponDto(
    Guid Id, string Code, string Kind, long Value, long? MinSubtotal, string? Section, int? MaxUses, int UsedCount, int? PerCustomerLimit,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Description, bool Active);

public sealed record SaveCouponCommand(Guid? Id, CouponInput Coupon) : ICommand<Result<Guid>>;

public sealed record ListCouponsQuery : IQuery<IReadOnlyList<CouponDto>>;

public sealed record PublicQuoteQuery(QuoteRequest Request) : IQuery<Quote>;

public sealed record PriceQuery(IReadOnlyCollection<Guid> VariantIds) : IQuery<IReadOnlyDictionary<Guid, PriceInfo>>;

internal sealed class PriceQueryHandler(IPriceReader prices) : IRequestHandler<PriceQuery, IReadOnlyDictionary<Guid, PriceInfo>>
{
    public Task<IReadOnlyDictionary<Guid, PriceInfo>> Handle(PriceQuery request, CancellationToken cancellationToken) =>
        prices.GetCurrentAsync(request.VariantIds, cancellationToken);
}

internal sealed class SetPriceValidator : AbstractValidator<SetPriceCommand>
{
    public SetPriceValidator() => RuleFor(x => x.Amount).InclusiveBetween(0, 100_000_000);
}

/// <summary>Prices are history, not a field: a change closes the current row and opens a new one (the 30-day rule needs it).</summary>
internal sealed class PricingHandlers(RahiqDbContext db, IClock clock, IAuditLog audit, ICurrentActor actor, IEventPublisher events, ClaimsGuard guard, IOptions<StoreOptions> store, IQuoteService quotes)
    : IRequestHandler<SetPriceCommand, Result>,
      IRequestHandler<PriceHistoryQuery, IReadOnlyList<PriceHistoryRow>>,
      IRequestHandler<SaveCouponCommand, Result<Guid>>,
      IRequestHandler<ListCouponsQuery, IReadOnlyList<CouponDto>>,
      IRequestHandler<PublicQuoteQuery, Quote>
{
    public async Task<Result> Handle(SetPriceCommand request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10)); // PostgreSQL keeps microseconds; compare at that precision.
        var currency = store.Value.Currency;
        var current = await db.Set<PriceEntry>()
            .FromSql($"SELECT * FROM pricing.prices WHERE variant_id = {request.VariantId} AND currency = {currency} AND valid_to IS NULL FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken);

        if (current?.Amount == request.Amount)
        {
            return Result.Success();
        }

        if (current is not null)
        {
            // Two changes within the same instant still leave a valid, strictly ordered history.
            now = now > current.ValidFrom ? now : current.ValidFrom.AddTicks(10);
            current.ValidTo = now;
        }

        db.Add(new PriceEntry { VariantId = request.VariantId, Currency = currency, Amount = request.Amount, ValidFrom = now, SetBy = actor.Id });
        audit.Record("price.changed", "variant", request.VariantId.ToString(), new { from = current?.Amount, to = request.Amount, currency });
        events.Publish(new PriceChanged([request.VariantId]));
        return Result.Success();
    }

    public async Task<IReadOnlyList<PriceHistoryRow>> Handle(PriceHistoryQuery request, CancellationToken cancellationToken) =>
        await db.Set<PriceEntry>().AsNoTracking().Where(p => p.VariantId == request.VariantId).OrderByDescending(p => p.ValidFrom)
            .Select(p => new PriceHistoryRow(p.Amount, p.ValidFrom, p.ValidTo)).ToListAsync(cancellationToken);

    public async Task<Result<Guid>> Handle(SaveCouponCommand request, CancellationToken cancellationToken)
    {
        var c = request.Coupon;
        var findings = guard.Check("coupon.description", c.Description);
        if (findings.Any(f => f.Severity == FindingSeverity.Blocking))
        {
            return Error.Conflict("coupon.forbidden_claims", "The description contains a forbidden health claim.");
        }

        var spec = new CouponSpec(c.Code, c.Kind, c.Value, store.Value.Currency, c.MinSubtotal, c.Section, c.MaxUses, c.PerCustomerLimit, c.StartsAt, c.EndsAt, c.Description, c.Active);
        var code = Coupon.NormalizeCode(c.Code);
        if (await db.Set<Coupon>().AnyAsync(x => x.Code == code && x.Id != request.Id, cancellationToken))
        {
            return Error.Conflict("coupon.code_taken", "This code already exists.");
        }

        if (request.Id is { } id)
        {
            var existing = await db.Set<Coupon>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (existing is null)
            {
                return Error.NotFound("coupon.not_found", "Coupon not found.");
            }

            var applied = existing.Apply(spec);
            if (applied.IsFailure)
            {
                return applied.Error;
            }

            audit.Record("coupon.updated", "coupon", id.ToString(), c);
            return id;
        }

        var created = Coupon.Create(spec, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error;
        }

        db.Add(created.Value);
        audit.Record("coupon.created", "coupon", created.Value.Id.ToString(), c);
        return created.Value.Id;
    }

    public async Task<IReadOnlyList<CouponDto>> Handle(ListCouponsQuery request, CancellationToken cancellationToken) =>
        [.. (await db.Set<Coupon>().AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync(cancellationToken))
            .Select(c => new CouponDto(c.Id, c.Code, c.Kind, c.Value, c.MinSubtotal, c.Section, c.MaxUses, c.UsedCount, c.PerCustomerLimit, c.StartsAt, c.EndsAt, c.Description, c.Active))];

    public Task<Quote> Handle(PublicQuoteQuery request, CancellationToken cancellationToken) => quotes.QuoteAsync(request.Request, cancellationToken);
}
