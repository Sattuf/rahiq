using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rahiq.SharedKernel;

namespace Rahiq.Infrastructure.Common.Web;

/// <summary>Thin controllers (ADR-003): bind, send, map the result. No business logic here.</summary>
[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected IActionResult FromResult(Result result) =>
        result.IsSuccess ? NoContent() : ProblemFor(result.Error);

    protected IActionResult FromResult<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : ProblemFor(result.Error);

    protected IActionResult Created<T>(Result<T> result, Func<T, string> location) =>
        result.IsSuccess ? base.Created(location(result.Value), result.Value) : ProblemFor(result.Error);

    protected static ObjectResult ProblemFor(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = StatusFor(error.Type);
        var problem = new ProblemDetails { Status = status, Title = error.Code, Detail = error.Message };
        problem.Extensions["code"] = error.Code;
        if (error.Details is not null)
        {
            problem.Extensions["errors"] = error.Details;
        }

        return new ObjectResult(problem) { StatusCode = status };
    }

    /// <summary>The store language of this request (?locale= or Accept-Language), defaulting to Turkish.</summary>
    protected string Locale =>
        Locales.OrDefault(Request.Query["locale"].FirstOrDefault()
            ?? Request.Headers.AcceptLanguage.FirstOrDefault()?.Split(',')[0].Split('-')[0]);

    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status422UnprocessableEntity,
    };
}
