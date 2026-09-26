using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Rahiq.Infrastructure.Common.Web;

/// <summary>
/// Modules keep their types internal (ADR-013); controllers that depend on them can be internal too.
/// This makes MVC discover internal controllers in addition to public ones.
/// </summary>
public sealed class InternalControllerFeatureProvider : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return typeInfo is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
            && !typeInfo.IsDefined(typeof(NonControllerAttribute))
            && typeof(ControllerBase).IsAssignableFrom(typeInfo);
    }
}
