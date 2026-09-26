using System.Data.Common;

namespace Rahiq.Application.Abstractions;

/// <summary>
/// The connection and transaction of the current unit of work, for the few places where hand-written SQL
/// is the correct tool (atomic stock reservation, coupon usage). Anything run through it joins the command's transaction.
/// </summary>
public interface IDbSession
{
    DbConnection Connection { get; }

    DbTransaction? Transaction { get; }

    Task EnsureOpenAsync(CancellationToken cancellationToken);

    /// <summary>Flushes tracked EF changes into the current transaction (so hand-written SQL can see them).</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>Object storage (Cloudflare R2 in production, MinIO locally, the file system in tests).</summary>
public interface IBlobStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    Task<Stream?> GetAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Public URL for assets served from the CDN, or a signed short-lived URL for private ones.</summary>
    Uri GetUrl(string key, TimeSpan? signedFor = null);
}

public sealed record StoreOptions
{
    public const string Section = "Store";

    /// <summary>Public base URL of the storefront (links in emails, QR codes, payment callbacks).</summary>
    public string PublicWebUrl { get; init; } = "http://localhost:3000";

    /// <summary>Public base URL of the API (payment and carrier callbacks).</summary>
    public string PublicApiUrl { get; init; } = "http://localhost:5080";

    public string Currency { get; init; } = "TRY";

    public int ReservationMinutes { get; init; } = 15;

    /// <summary>A batch that expires sooner than this is not sold (product-domain.md §4.2).</summary>
    public int MinShelfLifeDays { get; init; } = 60;

    public int NearExpiryAlertDays { get; init; } = 90;

    public int MaxQuantityPerVariant { get; init; } = 10;

    public int MaxSamplesPerOrder { get; init; } = 3;

    public long CodFee { get; init; } = 4_990;

    public long CodMaxOrderTotal { get; init; } = 500_000;

    public long RefundApprovalThreshold { get; init; } = 250_000;

    public int ReturnWindowDays { get; init; } = 14;
}
