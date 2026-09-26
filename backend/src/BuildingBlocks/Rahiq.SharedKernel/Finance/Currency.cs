namespace Rahiq.SharedKernel;

/// <summary>ISO 4217 currency code. Every amount in the system carries one explicitly (Law 5).</summary>
public readonly record struct Currency
{
    public static readonly Currency TRY = new("TRY");
    public static readonly Currency USD = new("USD");
    public static readonly Currency EUR = new("EUR");

    private Currency(string code) => Code = code;

    public string Code { get; }

    public static Currency From(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException($"'{code}' is not an ISO 4217 currency code.", nameof(code));
        }

        return new Currency(normalized);
    }

    public override string ToString() => Code;
}
