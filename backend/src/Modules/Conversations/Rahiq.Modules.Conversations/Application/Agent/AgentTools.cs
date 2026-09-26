using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Cart.Contracts;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Conversations.Domain;
using Rahiq.Modules.Ordering.Contracts;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Conversations.Application.Agent;

/// <summary>Who the assistant is talking to in this turn. Tools act for this conversation only.</summary>
internal sealed record AgentContext(Guid ConversationId, string Channel, string? CustomerName, string? VerifiedPhone, string? Locale);

internal sealed record ToolResult(string Json, bool IsError, bool HandedOff = false);

/// <summary>
/// The only things the assistant can do. Each tool is our own code with its own checks and limits: the model asks,
/// this class decides. Nothing here can change an order, a price or a payment, or reveal another customer's data.
/// </summary>
internal sealed class AgentTools(
    ICatalogSearch catalog,
    IOrderReader orders,
    ISender sender,
    IDbSession session,
    IOptions<StoreOptions> store,
    IOptions<AgentOptions> options)
{
    public const string SearchProducts = "search_products";
    public const string CheckStock = "check_stock";
    public const string CreateCheckoutLink = "create_checkout_link";
    public const string GetOrderStatus = "get_order_status";
    public const string HandoffToHuman = "handoff_to_human";

    private const int MaxLinksPerHour = 5;
    private const int MaxFailedOrderLookupsPerDay = 5;
    private const int LowStock = 3;

    public async Task<ToolResult> RunAsync(AgentContext context, string tool, JsonElement input, CancellationToken cancellationToken)
    {
        ToolResult result;
        try
        {
            result = tool switch
            {
                SearchProducts => await Search(input, cancellationToken),
                CheckStock => await Stock(input, cancellationToken),
                CreateCheckoutLink => await Link(context, input, cancellationToken),
                GetOrderStatus => await Order(context, input, cancellationToken),
                HandoffToHuman => await Handoff(context, input, cancellationToken),
                _ => Error($"Unknown tool {tool}."),
            };
        }
        catch (Exception ex) when (ex is ToolInputException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            // Bad input from the model: say so, so it can correct itself. Never a crash of the turn.
            result = Error(ex is ToolInputException ? ex.Message : "The input was not valid for this tool.");
        }

        await Record(context.ConversationId, tool, input, !result.IsError, cancellationToken);
        return result;
    }

    private async Task<ToolResult> Search(JsonElement input, CancellationToken cancellationToken)
    {
        var query = Text(input, "query", 100, required: false);
        var section = Text(input, "section", 20, required: false) is { } s && Sections.All.Contains(s) ? s : null;
        var language = Language(input);
        var matches = await catalog.SearchAsync(query, section, language, 6, cancellationToken);

        return Ok(new
        {
            results = matches.Select(p => new
            {
                name = p.Name,
                section = p.Section,
                type = p.Type,
                description = p.ShortDescription,
                url = $"{store.Value.PublicWebUrl.TrimEnd('/')}/{language}/p/{p.Slug}",
                variants = p.Variants.Select(Offer),
            }),
            note = matches.Count == 0 ? "No product matched. Try other words, a broader search, or no query to list everything." : null,
        });
    }

    private async Task<ToolResult> Stock(JsonElement input, CancellationToken cancellationToken)
    {
        var ids = Ids(input, "variant_ids", 10);
        var offers = await catalog.GetOffersAsync(ids, Language(input), cancellationToken);
        return Ok(new
        {
            variants = ids.Select(id => offers.TryGetValue(id, out var o)
                ? (object)Offer(o)
                : new { variant_id = id, availability = "not_sold", note = "Unknown or no longer sold." }),
        });
    }

    private async Task<ToolResult> Link(AgentContext context, JsonElement input, CancellationToken cancellationToken)
    {
        if (await RecentActions(context.ConversationId, CreateCheckoutLink, TimeSpan.FromHours(1), okOnly: true, cancellationToken) >= MaxLinksPerHour)
        {
            return Error("Too many checkout links in the last hour. Ask the customer to use the last link, or hand off to a person.");
        }

        if (!input.TryGetProperty("items", out var itemsJson) || itemsJson.ValueKind != JsonValueKind.Array)
        {
            throw new ToolInputException("items is required.");
        }

        var items = itemsJson.EnumerateArray().Take(11).Select(i => new HandoffItem(
            Guid.Parse(i.GetProperty("variant_id").GetString()!),
            i.GetProperty("quantity").GetInt32())).ToList();
        if (items.Count is 0 or > 10 || items.Any(i => i.Qty is < 1 or > 10))
        {
            throw new ToolInputException("Give 1 to 10 items, each with a quantity from 1 to 10.");
        }

        var created = await sender.Send(new CreateCheckoutLinkCommand(context.ConversationId, items, Language(input)), cancellationToken);
        if (created.IsFailure)
        {
            return Error(created.Error.Message);
        }

        var link = created.Value;
        return Ok(new
        {
            url = link.Url,
            valid_until = link.ExpiresAt.ToString("u", CultureInfo.InvariantCulture),
            items_in_cart = link.Lines,
            not_added = link.Skipped,
            next_step = "Send this link. The customer reviews the cart, enters the address, accepts the sales contract and pays on the website. Nothing is reserved until they pay; shipping cost is shown at checkout.",
        });
    }

    private async Task<ToolResult> Order(AgentContext context, JsonElement input, CancellationToken cancellationToken)
    {
        if (await RecentActions(context.ConversationId, GetOrderStatus, TimeSpan.FromDays(1), okOnly: false, failedOnly: true, cancellationToken) >= MaxFailedOrderLookupsPerDay)
        {
            return Error("Too many unsuccessful order lookups today. Hand off to a person.");
        }

        var number = Text(input, "order_number", 30, required: true)!.Trim().ToUpperInvariant();
        var proof = Text(input, "email_or_phone", 254, required: false);
        var order = await orders.GetByNumberAsync(number, cancellationToken);

        // The same answer for "no such order" and "details do not match", so the tool cannot be used to probe order numbers.
        if (order is null || !OrderVerification.Matches(order.Email, order.Phone, proof, context.VerifiedPhone))
        {
            return Error("No order matches this number together with that e-mail or phone. Ask the customer to check both (the ones used when ordering).");
        }

        return Ok(new
        {
            number = order.Number,
            status = order.Status,
            status_meaning = StatusMeaning(order.Status),
            placed_at = order.PlacedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            payment = order.PaymentMethod == PaymentMethods.CashOnDelivery ? "cash on delivery" : "card",
            total = Money(order.Total, order.Currency),
            items = order.Lines.Select(l => new { name = l.Name, variant = l.VariantLabel, qty = l.Qty }),
            shipping_to_province = order.ShippingAddress.ProvinceName,
            tracking = "The tracking link is in the shipping e-mail, and on the website's order tracking page.",
        });
    }

    private async Task<ToolResult> Handoff(AgentContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var reason = Text(input, "reason", 40, required: true)!;
        if (!HandoffReasons.AgentChoices.Contains(reason))
        {
            reason = "other";
        }

        var summary = Text(input, "summary", 600, required: false);
        await sender.Send(new HandOffCommand(context.ConversationId, reason, summary), cancellationToken);
        return new ToolResult(JsonSerializer.Serialize(new
        {
            ok = true,
            next_step = "A team member will continue this conversation. Tell the customer briefly, in their language, that a colleague will reply here soon. Do not promise a time.",
        }), false, HandedOff: true);
    }

    private object Offer(VariantOffer o) => new
    {
        variant_id = o.VariantId,
        product = o.ProductName,
        variant = o.Label,
        price = o.Price is { } p ? Money(p, o.Currency) : "no price",
        availability = o.Available <= 0 ? "out_of_stock" : o.Available <= LowStock ? $"only {o.Available} left" : "in_stock",
        sample = o.IsSample ? true : (bool?)null,
        note = o.IsGiftBox ? "Gift box: the customer chooses its contents on the website (send the product url)." : null,
    };

    private static string Money(long minor, string currency) =>
        $"{(minor / 100m).ToString("0.00", CultureInfo.InvariantCulture)} {currency}";

    private static string StatusMeaning(string status) => status switch
    {
        OrderStatuses.PendingPayment => "Waiting for payment; it is cancelled automatically if not paid.",
        OrderStatuses.Confirmed => "Paid and confirmed; will be prepared soon.",
        OrderStatuses.Preparing => "Being packed.",
        OrderStatuses.Shipped => "Handed to the carrier.",
        OrderStatuses.Delivered => "Delivered.",
        OrderStatuses.ReturnedToSender => "The parcel came back to us.",
        OrderStatuses.ReturnRequested => "A return was requested and is being reviewed.",
        OrderStatuses.Returned => "Returned.",
        OrderStatuses.Refunded => "Refunded.",
        OrderStatuses.Cancelled => "Cancelled.",
        _ => status,
    };

    private string Language(JsonElement input) =>
        Locales.OrDefault(Text(input, "language", 5, required: false) ?? options.Value.DefaultLanguage);

    private static string? Text(JsonElement input, string name, int max, bool required)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
        {
            return s.Length > max ? s[..max] : s;
        }

        return required ? throw new ToolInputException($"{name} is required.") : null;
    }

    private static List<Guid> Ids(JsonElement input, string name, int max)
    {
        if (!input.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            throw new ToolInputException($"{name} is required.");
        }

        var ids = v.EnumerateArray().Select(e => Guid.TryParse(e.GetString(), out var id) ? id : throw new ToolInputException("Use variant_id values from search results.")).Distinct().ToList();
        return ids.Count is > 0 && ids.Count <= max ? ids : throw new ToolInputException($"Give 1 to {max} ids.");
    }

    private static ToolResult Ok(object value) => new(JsonSerializer.Serialize(value, JsonDefaults.Options), false);

    private static ToolResult Error(string message) => new(JsonSerializer.Serialize(new { error = message }), true);

    private async Task<int> RecentActions(Guid conversationId, string tool, TimeSpan window, bool okOnly, CancellationToken cancellationToken) =>
        await RecentActions(conversationId, tool, window, okOnly, false, cancellationToken);

    private async Task<int> RecentActions(Guid conversationId, string tool, TimeSpan window, bool okOnly, bool failedOnly, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(*) FROM crm.agent_actions
            WHERE conversation_id = @conversationId AND tool = @tool AND at > now() - @window
              AND (NOT @okOnly OR ok) AND (NOT @failedOnly OR NOT ok)
            """, new { conversationId, tool, window, okOnly, failedOnly }, session.Transaction, cancellationToken: cancellationToken));
    }

    private async Task Record(Guid conversationId, string tool, JsonElement input, bool ok, CancellationToken cancellationToken)
    {
        await session.EnsureOpenAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO crm.agent_actions (conversation_id, tool, input, ok) VALUES (@conversationId, @tool, @input::jsonb, @ok)",
            new { conversationId, tool = tool.Length > 60 ? tool[..60] : tool, input = input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText(), ok },
            session.Transaction, cancellationToken: cancellationToken));
    }
}

internal sealed class ToolInputException(string message) : Exception(message)
{
    public ToolInputException()
        : this("Invalid input.")
    {
    }

    public ToolInputException(string message, Exception inner)
        : this(message) => _ = inner;
}

/// <summary>
/// Order details are shown only to someone who knows the order number AND the e-mail or phone used for it (or who writes
/// from that phone on WhatsApp / shared it on Telegram). Phones compare on their last 10 digits (Turkish national number).
/// </summary>
internal static class OrderVerification
{
    public static bool Matches(string orderEmail, string? orderPhone, string? proof, string? verifiedPhone)
    {
        if (PhonesMatch(orderPhone, verifiedPhone))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(proof))
        {
            return false;
        }

        proof = proof.Trim();
        return proof.Contains('@', StringComparison.Ordinal)
            ? string.Equals(proof, orderEmail, StringComparison.OrdinalIgnoreCase)
            : PhonesMatch(orderPhone, proof);
    }

    private static bool PhonesMatch(string? a, string? b)
    {
        var x = Digits(a);
        var y = Digits(b);
        return x.Length >= 10 && y.Length >= 10 && x[^10..] == y[^10..];
    }

    private static string Digits(string? value) => value is null ? string.Empty : new string([.. value.Where(char.IsAsciiDigit)]);
}
