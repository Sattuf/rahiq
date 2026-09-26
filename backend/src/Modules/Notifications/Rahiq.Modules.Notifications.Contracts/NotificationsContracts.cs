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
/// <param name="DedupeKey">
/// For recurring checks: alerts with the same key are e-mailed once (e.g. "batch.no_lab_report:2026-09-26"), so a
/// restart or a second run the same day does not send the same list again. Null means every alert is sent.
/// </param>
public sealed record StaffAlertRaised(string Kind, string Subject, string Body, string? Reference = null, string? DedupeKey = null) : DomainEvent;
