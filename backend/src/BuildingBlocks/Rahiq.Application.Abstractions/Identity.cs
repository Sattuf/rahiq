namespace Rahiq.Application.Abstractions;

public enum ActorKind
{
    Anonymous,
    Customer,
    Staff,
    System,
}

/// <summary>Who is making the request, read from the validated access token.</summary>
public interface ICurrentActor
{
    ActorKind Kind { get; }

    Guid? Id { get; }

    string? Email { get; }

    bool HasPermission(string permission);
}

/// <summary>Fine-grained admin permissions (security.md §3).</summary>
public static class Permissions
{
    public const string ProductsEdit = "products.edit";
    public const string ProductsPublish = "products.publish";
    public const string ClaimsOverride = "claims.override";
    public const string PricesEdit = "prices.edit";
    public const string InventoryEdit = "inventory.edit";
    public const string OrdersView = "orders.view";
    public const string OrdersFulfil = "orders.fulfil";
    public const string OrdersCancel = "orders.cancel";
    public const string OrdersRefund = "orders.refund";
    public const string OrdersRefundLarge = "orders.refund.large";
    public const string ReturnsProcess = "returns.process";
    public const string CouponsEdit = "coupons.edit";
    public const string ContentEdit = "content.edit";
    public const string CustomersView = "customers.view";
    public const string StaffManage = "staff.manage";
    public const string AuditView = "audit.view";
    public const string ReportsView = "reports.view";
    public const string ConversationsView = "conversations.view";
    public const string ConversationsReply = "conversations.reply";

    public static readonly IReadOnlyList<string> All =
    [
        ProductsEdit, ProductsPublish, ClaimsOverride, PricesEdit, InventoryEdit, OrdersView, OrdersFulfil,
        OrdersCancel, OrdersRefund, OrdersRefundLarge, ReturnsProcess, CouponsEdit, ContentEdit, CustomersView,
        StaffManage, AuditView, ReportsView, ConversationsView, ConversationsReply,
    ];
}

/// <summary>The five admin roles (security.md §3) as fixed maps to permissions.</summary>
public static class StaffRoles
{
    public const string Owner = "owner";
    public const string Manager = "manager";
    public const string Content = "content";
    public const string Fulfillment = "fulfillment";
    public const string Support = "support";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> PermissionsByRole =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [Owner] = Permissions.All,
            [Manager] =
            [
                Permissions.ProductsEdit, Permissions.ProductsPublish, Permissions.ClaimsOverride, Permissions.PricesEdit,
                Permissions.InventoryEdit, Permissions.OrdersView, Permissions.OrdersFulfil, Permissions.OrdersCancel,
                Permissions.OrdersRefund, Permissions.ReturnsProcess, Permissions.CouponsEdit, Permissions.ContentEdit,
                Permissions.CustomersView, Permissions.AuditView, Permissions.ReportsView,
                Permissions.ConversationsView, Permissions.ConversationsReply,
            ],
            [Content] = [Permissions.ProductsEdit, Permissions.ContentEdit, Permissions.CouponsEdit],
            [Fulfillment] = [Permissions.OrdersView, Permissions.OrdersFulfil, Permissions.InventoryEdit, Permissions.ReturnsProcess],
            [Support] = [Permissions.OrdersView, Permissions.CustomersView, Permissions.ReturnsProcess, Permissions.ConversationsView, Permissions.ConversationsReply],
        };

    public static bool IsValid(string role) => PermissionsByRole.ContainsKey(role);
}

/// <summary>Append-only audit trail for sensitive admin actions (security.md §6).</summary>
public interface IAuditLog
{
    void Record(string action, string entity, string? entityId, object? data = null);
}
