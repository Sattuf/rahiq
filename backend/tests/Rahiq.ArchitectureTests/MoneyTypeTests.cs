namespace Rahiq.ArchitectureTests;

/// <summary>Money never touches floating point (Law 5, ADR-004): no double or float field or property in any module.</summary>
public class MoneyTypeTests
{
    [Fact]
    public void No_module_stores_money_or_quantities_as_floating_point()
    {
        var offenders = AppDomain.CurrentDomain.GetAssemblies()
            .Concat(new[] { "Catalog", "Inventory", "Pricing", "Cart", "Ordering", "Payments", "Shipping" }.Select(m => System.Reflection.Assembly.Load($"Rahiq.Modules.{m}")))
            .Where(a => a.GetName().Name!.StartsWith("Rahiq.Modules", StringComparison.Ordinal))
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetProperties().Select(p => (t, Name: p.Name, Type: p.PropertyType)))
            .Where(x => x.Type == typeof(double) || x.Type == typeof(float) || x.Type == typeof(double?) || x.Type == typeof(float?))
            .Select(x => $"{x.t.FullName}.{x.Name}")
            .ToList();

        Assert.Empty(offenders);
    }
}
