using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.Modules.Payments.Application;
using Rahiq.Modules.Payments.Domain;
using Rahiq.Modules.Payments.Infrastructure;

namespace Rahiq.Modules.Payments.Presentation;

/// <summary>
/// Provider notifications. The raw body is read before anything else so the signature is checked over the exact bytes.
/// A duplicate answers 200 and does nothing; an unverifiable one answers 401 and changes nothing.
/// </summary>
[Route("webhooks/payments")]
[ApiExplorerSettings(IgnoreApi = true)]
internal sealed class PaymentWebhookController(ISender sender, IEnumerable<IPaymentProvider> providers, IOptions<StoreOptions> store) : ApiControllerBase
{
    [HttpPost("{provider}")]
    public async Task<IActionResult> Receive(string provider, CancellationToken ct)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var form = new Dictionary<string, string>();
        if (Request.HasFormContentType)
        {
            foreach (var (key, value) in await Request.ReadFormAsync(ct))
            {
                form[key] = value.ToString();
            }
        }

        var headers = Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
        var result = await sender.Send(new ReceiveWebhookCommand(provider, body, form, headers), ct);

        // iyzico returns the buyer's browser to this URL: send them to the result page, which polls the order.
        if (providers.FirstOrDefault(p => p.Name == provider)?.CallbackIsBrowser == true)
        {
            var number = result.IsSuccess ? result.Value.OrderNumber : null;
            return Redirect($"{store.Value.PublicWebUrl.TrimEnd('/')}/checkout/result{(number is null ? string.Empty : $"?order={Uri.EscapeDataString(number)}")}");
        }

        return result.IsSuccess ? Ok(new { result.Value.Status }) : ProblemFor(result.Error);
    }
}

/// <summary>Development and staging only: the sandbox provider's hosted payment page.</summary>
[Route("dev/payments/sandbox")]
[ApiExplorerSettings(IgnoreApi = true)]
internal sealed class SandboxPaymentPageController(RahiqDbContext db, SandboxPaymentProvider sandbox, IOptions<StoreOptions> store, IHostEnvironment environment)
    : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Page(string token, CancellationToken ct)
    {
        if (environment.IsProduction())
        {
            return NotFound();
        }

        var payment = await db.Set<Payment>().AsNoTracking().FirstOrDefaultAsync(p => p.Provider == "sandbox" && p.SessionToken == token, ct);
        if (payment is null)
        {
            return NotFound();
        }

        var amount = $"{payment.Amount / 100m:0.00} {payment.Currency}";
        var html = $$"""
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Sandbox payment</title>
            <style>body{font:16px system-ui;background:#f4f4f5;margin:0;display:grid;place-items:center;min-height:100vh}
            form{background:#fff;padding:24px;border-radius:12px;max-width:360px;width:calc(100% - 32px);box-shadow:0 2px 12px #0001}
            button{display:block;width:100%;padding:12px;margin-top:10px;border-radius:8px;border:1px solid #ccc;font:inherit;cursor:pointer}
            .ok{background:#1f7a3d;color:#fff;border:0}</style>
            <form method="post">
              <h1 style="font-size:18px">Sandbox card payment</h1>
              <p>Order <strong>{{WebUtility.HtmlEncode(payment.OrderNumber)}}</strong><br>Amount <strong>{{amount}}</strong></p>
              <label>Instalments <select name="installments"><option>1</option><option>2</option><option>3</option><option>6</option><option>9</option></select></label>
              <button class="ok" name="outcome" value="succeeded" data-testid="sandbox-pay">Pay {{amount}} (3-D Secure OK)</button>
              <button name="outcome" value="failed" data-testid="sandbox-fail">Decline the card</button>
            </form></html>
            """;
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpPost("{token}")]
    public async Task<IActionResult> Submit(string token, [FromForm] string outcome, [FromForm] int installments, CancellationToken ct)
    {
        if (environment.IsProduction())
        {
            return NotFound();
        }

        var payment = await db.Set<Payment>().AsNoTracking().FirstOrDefaultAsync(p => p.Provider == "sandbox" && p.SessionToken == token, ct);
        if (payment is null)
        {
            return NotFound();
        }

        await sandbox.SendAsync(new SandboxNotification(
            $"evt_{Guid.NewGuid():N}", token, outcome == "succeeded" ? "succeeded" : "failed", payment.Amount, payment.Currency,
            Math.Max(1, installments), payment.ProviderRef, outcome == "succeeded" ? null : "card_declined"), ct);

        return Redirect($"{store.Value.PublicWebUrl.TrimEnd('/')}/checkout/result?order={Uri.EscapeDataString(payment.OrderNumber)}");
    }
}
