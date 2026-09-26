using Rahiq.Modules.Inventory.Contracts;
using Rahiq.Modules.Inventory.Domain;

namespace Rahiq.Domain.Tests.Inventory;

public class FefoTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static BatchStock Batch(string code, int free, DateOnly? bestBefore) => new(Guid.NewGuid(), code, free, bestBefore);

    /// <summary>testing.md commerce test 10, at the domain level.</summary>
    [Fact]
    public void The_batch_that_expires_first_ships_first()
    {
        var takes = Fefo.Allocate([Batch("LATE", 10, Today.AddDays(400)), Batch("EARLY", 10, Today.AddDays(200))], 3, Today, 60)!;

        Assert.Equal("EARLY", Assert.Single(takes).Code);
    }

    [Fact]
    public void A_batch_too_close_to_expiry_is_never_picked()
    {
        var takes = Fefo.Allocate([Batch("SOON", 10, Today.AddDays(30)), Batch("OK", 10, Today.AddDays(300))], 2, Today, 60)!;

        Assert.Equal("OK", Assert.Single(takes).Code);
    }

    [Fact]
    public void Batches_without_a_date_go_last()
    {
        var takes = Fefo.Allocate([Batch("NODATE", 10, null), Batch("DATED", 1, Today.AddDays(300))], 3, Today, 60)!;

        Assert.Equal(["DATED", "NODATE"], takes.Select(t => t.Code));
        Assert.Equal([1, 2], takes.Select(t => t.Qty));
    }

    [Fact]
    public void Not_enough_stock_returns_null_rather_than_a_partial_pick()
    {
        Assert.Null(Fefo.Allocate([Batch("A", 2, null), Batch("B", 0, null)], 3, Today, 60));
    }
}

public class BatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Food_batches_need_a_best_before_date()
    {
        var result = Batch.Receive(Guid.NewGuid(), "KST-2609", 10, null, null, requiresExpiry: true, null, Now);

        Assert.Equal("batch.best_before_required", result.Error.Code);
    }

    [Fact]
    public void Best_before_must_follow_production()
    {
        var result = Batch.Receive(Guid.NewGuid(), "KST-2609", 10, new DateOnly(2026, 9, 1), new DateOnly(2026, 8, 1), true, null, Now);

        Assert.Equal("batch.best_before_invalid", result.Error.Code);
    }

    [Theory]
    [InlineData("", 10)]
    [InlineData("K", 10)]
    [InlineData("KST-2609", 0)]
    public void Code_and_quantity_are_validated(string code, int qty) =>
        Assert.True(Batch.Receive(Guid.NewGuid(), code, qty, null, new DateOnly(2027, 1, 1), true, null, Now).IsFailure);

    [Fact]
    public void Receiving_raises_an_event_and_gets_an_unguessable_token()
    {
        var batch = Batch.Receive(Guid.NewGuid(), "kst-2609", 24, new DateOnly(2026, 9, 1), new DateOnly(2028, 9, 1), true, null, Now).Value;

        Assert.Equal("KST-2609", batch.Code);
        Assert.Equal(24, batch.QtyOnHand);
        Assert.Equal(24, batch.Free);
        Assert.Equal(24, batch.PublicToken.Length);
        Assert.IsType<BatchReceived>(Assert.Single(batch.DomainEvents));
        Assert.NotEqual(Batch.NewPublicToken(), Batch.NewPublicToken());
    }

    [Fact]
    public void Adjustments_need_a_reason_and_cannot_drop_below_reserved()
    {
        var batch = Batch.Receive(Guid.NewGuid(), "B-1", 10, null, null, false, null, Now).Value;

        Assert.Equal("batch.adjustment_note_required", batch.AdjustOnHand(8, " ").Error.Code);
        Assert.Equal(-2, batch.AdjustOnHand(8, "Monthly count: 2 broken jars").Value);
        Assert.Equal(8, batch.QtyOnHand);
    }

    [Fact]
    public void Sellable_respects_minimum_shelf_life()
    {
        var batch = Batch.Receive(Guid.NewGuid(), "B-1", 10, null, new DateOnly(2026, 11, 1), true, null, Now).Value;

        Assert.False(batch.IsSellable(new DateOnly(2026, 9, 25), 60));
        Assert.True(batch.IsSellable(new DateOnly(2026, 8, 1), 60));
    }

    [Fact]
    public void Lab_report_and_origin_can_be_attached()
    {
        var batch = Batch.Receive(Guid.NewGuid(), "B-1", 10, null, null, false, null, Now).Value;
        using var summary = System.Text.Json.JsonDocument.Parse("""{"moisture":17.2}""");
        using var origin = System.Text.Json.JsonDocument.Parse("""{"region":"Rize"}""");

        batch.AttachLabReport("lab/b-1.pdf", summary);
        batch.UpdateOrigin(origin);
        batch.UpdateLabSummary(summary);

        Assert.Equal("lab/b-1.pdf", batch.LabReportKey);
        Assert.NotNull(batch.Origin);
    }
}
