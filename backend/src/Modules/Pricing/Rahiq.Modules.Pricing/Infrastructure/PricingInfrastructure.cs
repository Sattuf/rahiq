using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Pricing.Contracts;
using Rahiq.Modules.Pricing.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Pricing.Infrastructure;

internal sealed class TaxRate
{
    public required string Category { get; init; }

    public int RateBp { get; init; }

    public DateOnly ValidFrom { get; init; }
}

internal sealed class CouponRedemption
{
    public Guid Id { get; init; }

    public Guid CouponId { get; init; }

    public Guid OrderId { get; init; }

    public required string CustomerKey { get; init; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class PricingModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceEntry>(b =>
        {
            b.ToTable("prices", "pricing");
            b.HasKey(p => new { p.VariantId, p.Currency, p.ValidFrom });
            b.Property(p => p.Currency).HasColumnType("char(3)");
        });

        modelBuilder.Entity<TaxRate>(b =>
        {
            b.ToTable("tax_rates", "pricing");
            b.HasKey(t => new { t.Category, t.ValidFrom });
        });

        modelBuilder.Entity<Coupon>(b =>
        {
            b.ToTable("coupons", "pricing");
            b.HasKey(c => c.Id);
            b.Ignore(c => c.DomainEvents);
            b.Property(c => c.Code).HasColumnType("citext");
            b.Property(c => c.Currency).HasColumnType("char(3)");
        });

        modelBuilder.Entity<CouponRedemption>(b =>
        {
            b.ToTable("coupon_redemptions", "pricing");
            b.HasKey(r => r.Id);
        });
    }
}

internal sealed class PriceReader(RahiqDbContext db, IClock clock, IOptions<StoreOptions> store) : IPriceReader
{
    public async Task<IReadOnlyDictionary<Guid, PriceInfo>> GetCurrentAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, PriceInfo>();
        }

        var now = clock.UtcNow;
        var since = now - PreviousPriceRule.Window - TimeSpan.FromDays(400);
        var currencyCode = store.Value.Currency;
        var ids = variantIds.Distinct().ToList();
        var history = await db.Set<PriceEntry>().AsNoTracking()
            .Where(p => ids.Contains(p.VariantId) && p.Currency == currencyCode && (p.ValidTo == null || p.ValidTo > since))
            .ToListAsync(cancellationToken);
        var currency = Currency.From(currencyCode);

        var result = new Dictionary<Guid, PriceInfo>();
        foreach (var group in history.GroupBy(p => p.VariantId))
        {
            var list = group.ToList();
            var current = list.FirstOrDefault(p => p.IsActiveAt(now));
            if (current is null)
            {
                continue;
            }

            var previous = PreviousPriceRule.Compute(list, now);
            result[group.Key] = new PriceInfo(group.Key, Money.Of(current.Amount, currency), previous is null ? null : Money.Of(previous.Value, currency));
        }

        return result;
    }
}

/// <summary>Coupon usage held atomically at pay, like stock (ADR-016): the row update is the lock.</summary>
internal sealed class CouponUsage(RahiqDbContext db, IDbSession session, IClock clock) : ICouponUsage
{
    public async Task<Result> HoldAsync(string code, Guid orderId, string customerKey, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var normalized = Coupon.NormalizeCode(code);
        var coupon = await db.Set<Coupon>().AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
        if (coupon is null)
        {
            return CouponErrors.NotFound;
        }

        var usable = coupon.CheckUsable(clock.UtcNow);
        if (usable.IsFailure && usable.Error != CouponErrors.Exhausted)
        {
            return usable;
        }

        // Increment only while below the limit. Concurrent holds queue on this row; the loser sees 0 rows.
        var held = await session.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE pricing.coupons SET used_count = used_count + 1
            WHERE id = @id AND active AND (max_uses IS NULL OR used_count < max_uses)
            """, new { id = coupon.Id }, session.Transaction, cancellationToken: cancellationToken));
        if (held == 0)
        {
            return CouponErrors.Exhausted;
        }

        if (coupon.PerCustomerLimit is { } limit)
        {
            var used = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT count(*) FROM pricing.coupon_redemptions WHERE coupon_id = @id AND customer_key = @customerKey AND status <> 'released'
                """, new { id = coupon.Id, customerKey }, session.Transaction, cancellationToken: cancellationToken));
            if (used >= limit)
            {
                return CouponErrors.CustomerLimit; // The transaction rolls back the increment above.
            }
        }

        await session.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pricing.coupon_redemptions (id, coupon_id, order_id, customer_key, status) VALUES (@Id, @CouponId, @orderId, @customerKey, 'held')
            """, new { Id = Ids.New(), CouponId = coupon.Id, orderId, customerKey }, session.Transaction, cancellationToken: cancellationToken));
        return Result.Success();
    }

    public async Task CommitAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pricing.coupon_redemptions SET status = 'committed' WHERE order_id = @orderId AND status = 'held'",
            new { orderId }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("""
            WITH released AS (
              UPDATE pricing.coupon_redemptions SET status = 'released' WHERE order_id = @orderId AND status <> 'released' RETURNING coupon_id
            )
            UPDATE pricing.coupons c SET used_count = c.used_count - 1 FROM released r WHERE c.id = r.coupon_id
            """, new { orderId }, session.Transaction, cancellationToken: cancellationToken));
    }
}
