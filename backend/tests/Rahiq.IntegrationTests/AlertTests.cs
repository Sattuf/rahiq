using Rahiq.Modules.Inventory.Application;
using Rahiq.Testing;

namespace Rahiq.IntegrationTests;

public sealed class AlertTests(RahiqApp app) : IClassFixture<RahiqApp>
{
    [Fact]
    public async Task The_daily_stock_check_mails_one_digest_even_when_it_runs_again()
    {
        await app.CreateHoneyAsync(20_000, 5); // A food batch without a lab report.
        await app.CreateHoneyAsync(25_000, 5);

        // The job runs at every start: a restart the same day is a second run.
        RahiqApp.Ok(await app.Send(new RaiseInventoryAlertsCommand()));
        await app.DrainOutboxAsync();
        RahiqApp.Ok(await app.Send(new RaiseInventoryAlertsCommand()));
        await app.DrainOutboxAsync();

        var mails = await app.Scalar<int>("SELECT count(*)::int FROM notifications.log WHERE template = 'alert:batch.no_lab_report'");
        var recipients = await app.Scalar<int>("SELECT count(DISTINCT recipient_masked)::int FROM notifications.log WHERE template = 'alert:batch.no_lab_report'");
        Assert.Equal(recipients, mails); // One digest per recipient, not one per batch or per run.
        Assert.True(mails >= 1);
    }
}
