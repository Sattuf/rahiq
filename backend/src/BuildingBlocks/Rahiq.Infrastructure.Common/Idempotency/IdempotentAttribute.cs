using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;

namespace Rahiq.Infrastructure.Common.Idempotency;

/// <summary>
/// Requires an <c>Idempotency-Key</c> header on a money-moving endpoint (architecture.md §5). The first request
/// with a key runs; a repeat (double click, network retry) gets the stored response and runs nothing.
/// The key row is written on its own connection before the action, so two simultaneous duplicates cannot both run.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute, IFilterFactory
{
    public const string HeaderName = "Idempotency-Key";

    public bool IsReusable => true;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<IdempotencyFilter>(serviceProvider);
}

internal sealed class IdempotencyFilter(NpgsqlDataSource dataSource) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[IdempotentAttribute.HeaderName].ToString();
        if (key.Length is < 8 or > 100)
        {
            context.Result = Problem(StatusCodes.Status400BadRequest, "idempotency.key_required", "An Idempotency-Key header (8-100 characters) is required.");
            return;
        }

        var actor = http.RequestServices.GetRequiredService<ICurrentActor>();
        var scope = $"{context.ActionDescriptor.AttributeRouteInfo?.Template}|{actor.Kind}:{actor.Id}";
        var arguments = context.ActionArguments.Where(a => a.Value is not CancellationToken).ToDictionary(a => a.Key, a => a.Value);
        var requestHash = Hash(JsonSerializer.Serialize(arguments, JsonDefaults.Options));
        var cancellationToken = http.RequestAborted;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO infra.idempotency_keys (scope, key, request_hash) VALUES (@scope, @key, @hash) ON CONFLICT DO NOTHING",
            connection))
        {
            insert.Parameters.AddWithValue("scope", scope);
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.AddWithValue("hash", requestHash);

            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                context.Result = await Replay(connection, scope, key, requestHash, cancellationToken);
                return;
            }
        }

        var executed = await next();

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            // Nothing committed a result: free the key so the client can retry.
            await using var delete = new NpgsqlCommand("DELETE FROM infra.idempotency_keys WHERE scope = @scope AND key = @key", connection);
            delete.Parameters.AddWithValue("scope", scope);
            delete.Parameters.AddWithValue("key", key);
            await delete.ExecuteNonQueryAsync(CancellationToken.None);
            return;
        }

        var (status, body) = executed.Result switch
        {
            ObjectResult o => (o.StatusCode ?? StatusCodes.Status200OK, JsonSerializer.Serialize(o.Value, JsonDefaults.Options)),
            StatusCodeResult s => (s.StatusCode, "null"),
            _ => (StatusCodes.Status200OK, "null"),
        };

        await using var complete = new NpgsqlCommand(
            "UPDATE infra.idempotency_keys SET status_code = @status, response_body = @body::jsonb, completed_at = now() WHERE scope = @scope AND key = @key",
            connection);
        complete.Parameters.AddWithValue("status", status);
        complete.Parameters.AddWithValue("body", body);
        complete.Parameters.AddWithValue("scope", scope);
        complete.Parameters.AddWithValue("key", key);
        await complete.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<IActionResult> Replay(NpgsqlConnection connection, string scope, string key, string requestHash, CancellationToken cancellationToken)
    {
        await using var select = new NpgsqlCommand(
            "SELECT request_hash, status_code, response_body::text FROM infra.idempotency_keys WHERE scope = @scope AND key = @key",
            connection);
        select.Parameters.AddWithValue("scope", scope);
        select.Parameters.AddWithValue("key", key);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return Problem(StatusCodes.Status409Conflict, "idempotency.retry", "Please retry.");
        }

        if (reader.GetString(0) != requestHash)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "idempotency.key_reused", "This Idempotency-Key was used with a different request.");
        }

        if (reader.IsDBNull(1))
        {
            return Problem(StatusCodes.Status409Conflict, "idempotency.in_progress", "The same request is still being processed.");
        }

        var status = reader.GetInt32(1);
        JsonElement? body = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(2));
        return new ObjectResult(body) { StatusCode = status };
    }

    private static ObjectResult Problem(int status, string code, string detail) =>
        new(new ProblemDetails { Status = status, Title = code, Detail = detail, Extensions = { ["code"] = code } }) { StatusCode = status };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
