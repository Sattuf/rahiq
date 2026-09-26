using Rahiq.SharedKernel;

namespace Rahiq.Modules.Notifications.Contracts;

public static class AlertKinds
{
    public const string PaymentAmountMismatch = "payment.amount_mismatch";
    public const string LatePaymentRefund = "payment.late_refund";
    public const string ReconciliationDiscrepancy = "payment.reconciliation";
    public const string WebhookMissing = "payment.webhook_missing";
    public const string InvoiceFailed = "invoice.failed";
    public const string StockLow = "stock.low";
    public const string BatchNearExpiry = "batch.near_expiry";
    public const string BatchWithoutLabReport = "batch.no_lab_report";
    public const string RefundFailed = "refund.failed";
    public const string FraudReview = "order.fraud_review";
    public const string ConversationHandoff = "conversation.handoff";
}

/// <summary>Something staff must look at now (devops.md §4). Any module may raise one.</summary>
public sealed record StaffAlertRaised(string Kind, string Subject, string Body, string? Reference = null) : DomainEvent;
