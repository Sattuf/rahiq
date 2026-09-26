using System.Reflection;
using NetArchTest.Rules;

namespace Rahiq.ArchitectureTests;

/// <summary>ADR-001, ADR-012, ADR-013: modules touch each other only through Contracts; layers stay in order.</summary>
public class LayeringTests
{
    private static readonly string[] ModuleNames =
        ["Catalog", "Inventory", "Pricing", "Cart", "Ordering", "Payments", "Shipping", "Customers", "Content", "Documents", "Notifications", "Conversations"];

    public static TheoryData<string> Modules => [.. ModuleNames];

    private static Assembly Module(string name) => Assembly.Load($"Rahiq.Modules.{name}");

    private static Assembly Contracts(string name) => Assembly.Load($"Rahiq.Modules.{name}.Contracts");

    [Fact]
    public void SharedKernel_depends_on_no_other_rahiq_assembly()
    {
        var result = Types.InAssembly(typeof(SharedKernel.AssemblyReference).Assembly)
            .ShouldNot().HaveDependencyOnAny("Rahiq.Application", "Rahiq.Infrastructure", "Rahiq.Api", "Rahiq.Modules", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Application_abstractions_do_not_depend_on_infrastructure_or_api()
    {
        var result = Types.InAssembly(typeof(Application.Abstractions.AssemblyReference).Assembly)
            .ShouldNot().HaveDependencyOnAny("Rahiq.Infrastructure", "Rahiq.Api", "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void A_module_references_other_modules_only_through_their_contracts(string name)
    {
        var forbidden = ModuleNames.Where(m => m != name).Select(m => $"Rahiq.Modules.{m}").ToHashSet();
        var references = Module(name).GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, forbidden.Contains);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Contracts_depend_only_on_the_shared_kernel(string name)
    {
        var result = Types.InAssembly(Contracts(name))
            .ShouldNot().HaveDependencyOnAny("Rahiq.Infrastructure", "Rahiq.Application", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", $"Rahiq.Modules.{name}.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void The_domain_knows_nothing_of_databases_web_or_other_layers(string name)
    {
        var result = Types.InAssembly(Module(name)).That().ResideInNamespace($"Rahiq.Modules.{name}.Domain")
            .ShouldNot().HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "Dapper",
                $"Rahiq.Modules.{name}.Application", $"Rahiq.Modules.{name}.Infrastructure", $"Rahiq.Modules.{name}.Presentation")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Application_does_not_depend_on_presentation_or_mvc(string name)
    {
        var result = Types.InAssembly(Module(name)).That().ResideInNamespace($"Rahiq.Modules.{name}.Application")
            .ShouldNot().HaveDependencyOnAny($"Rahiq.Modules.{name}.Presentation", "Microsoft.AspNetCore.Mvc")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Controllers_live_in_presentation(string name)
    {
        var result = Types.InAssembly(Module(name)).That().Inherit(typeof(Microsoft.AspNetCore.Mvc.ControllerBase))
            .Should().ResideInNamespace($"Rahiq.Modules.{name}.Presentation")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
