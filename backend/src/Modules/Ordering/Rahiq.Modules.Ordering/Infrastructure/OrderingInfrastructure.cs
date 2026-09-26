using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.Modules.Ordering.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Ordering.Infrastructure;

internal sealed class OrderNote
{
    public Guid Id { get; init; }

    public Guid OrderId { get; init; }

    public required string Body { get; init; }

    public Guid AuthorId { get; init; }

    public DateTimeOffset At { get; init; }
}

internal sealed class OrderingModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders", "ordering");
            b.HasKey(o => o.Id);
            b.Ignore(o => o.DomainEvents);
            b.Ignore(o => o.CancelReason);
            b.Ignore(o => o.IsPaidByCard);
            b.Property(o => o.Email).HasColumnType("citext");
            b.Property(o => o.Currency).HasColumnType("char(3)");
            b.Property(o => o.ShippingAddress).HasColumnType("jsonb");
            b.Property(o => o.BillingAddress).HasColumnType("jsonb");
            b.Property(o => o.ContractDocHash).HasColumnName("contract_doc_hash");
            b.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId);
            b.HasMany(o => o.History).WithOne().HasForeignKey(h => h.OrderId);
            b.Navigation(o => o.Lines).HasField("_lines");
            b.Navigation(o => o.History).HasField("_history");
        });

        modelBuilder.Entity<OrderLine>(b =>
        {
            b.ToTable("order_lines", "ordering");
            b.HasKey(l => l.Id);
            b.Property(l => l.BatchAllocations).HasColumnType("jsonb");
        });

        modelBuilder.Entity<OrderStatusChange>(b =>
        {
            b.ToTable("order_status_history", "ordering");
            b.HasKey(h => h.Id);
            b.Property(h => h.Id).UseIdentityByDefaultColumn();
        });

        modelBuilder.Entity<Checkout>(b =>
        {
            b.ToTable("checkouts", "ordering");
            b.HasKey(c => c.Id);
            b.Ignore(c => c.IsComplete);
            b.Property(c => c.Email).HasColumnType("citext");
            b.Property(c => c.ShippingAddress).HasColumnType("jsonb");
            b.Property(c => c.BillingAddress).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ReturnRequest>(b =>
        {
            b.ToTable("return_requests", "ordering");
            b.HasKey(r => r.Id);
            b.Property(r => r.Lines).HasColumnType("jsonb");
        });

        modelBuilder.Entity<OrderNote>(b =>
        {
            b.ToTable("order_notes", "ordering");
            b.HasKey(n => n.Id);
        });
    }
}

/// <summary>Human-readable order numbers: RHQ-26-000123 (data-model.md §4), from a database sequence.</summary>
internal sealed class OrderNumbers(IDbSession session, IClock clock)
{
    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var next = await session.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT nextval('ordering.order_number_seq')", transaction: session.Transaction, cancellationToken: cancellationToken));
        return string.Create(CultureInfo.InvariantCulture, $"RHQ-{clock.UtcNow:yy}-{next:000000}");
    }
}

internal sealed class OrderReader(RahiqDbContext db) : IOrderReader
{
    public async Task<OrderInfo?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await Orders().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        return order is null ? null : ToInfo(order);
    }

    public async Task<OrderInfo?> GetByNumberAsync(string number, CancellationToken cancellationToken)
    {
        var normalized = number.Trim().ToUpperInvariant();
        var order = await Orders().FirstOrDefaultAsync(o => o.Number == normalized, cancellationToken);
        return order is null ? null : ToInfo(order);
    }

    public async Task<IReadOnlyList<OrderInfo>> ForCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        [.. (await Orders().Where(o => o.CustomerId == customerId).OrderByDescending(o => o.PlacedAt).ToListAsync(cancellationToken)).Select(ToInfo)];

    private IQueryable<Order> Orders() => db.Set<Order>().AsNoTracking().Include(o => o.Lines).AsSplitQuery();

    internal static OrderInfo ToInfo(Order o) => new()
    {
        Id = o.Id,
        Number = o.Number,
        Status = o.Status,
        CustomerId = o.CustomerId,
        Email = o.Email,
        Phone = o.Phone,
        Locale = o.Locale,
        PaymentMethod = o.PaymentMethod,
        Currency = o.Currency,
        Subtotal = o.Subtotal,
        Discount = o.Discount,
        Shipping = o.Shipping,
        CodFee = o.CodFee,
        Tax = o.Tax,
        Total = o.Total,
        ShippingMethod = o.ShippingMethod,
        ShippingAddress = o.ShippingAddress,
        BillingAddress = o.BillingAddress,
        IsGift = o.IsGift,
        GiftMessage = o.GiftMessage,
        HidePrices = o.HidePrices,
        PlacedAt = o.PlacedAt,
        Lines = [.. o.Lines.OrderBy(l => l.SortOrder).Select(l => new OrderLineInfo(
            l.Id, l.VariantId, l.Sku, l.NameSnapshot, l.VariantLabelSnapshot, l.ProductType, l.Qty, l.UnitPrice, l.Discount, l.LineTotal,
            l.TaxRateBp, l.TaxAmount, l.ShippingClass, l.GroupLabelSnapshot, l.WarningsSnapshot, l.BatchAllocations ?? []))],
    };
}
