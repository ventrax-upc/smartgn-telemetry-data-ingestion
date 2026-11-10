using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Smartgn.Telemetry.Configuration;

namespace Smartgn.Telemetry.Interfaces.Rest;

/// <summary>
/// Guards every internal route with the service-to-service credential, the
/// same <c>serviceAuth</c> scheme the sibling services publish in their
/// OpenAPI documents. An empty token keeps the routes open for local
/// development, as documented in the README.
/// </summary>
public sealed class InternalServiceTokenFilter(
    IntegrationSettings settings,
    ILogger<InternalServiceTokenFilter> logger) : IAsyncActionFilter
{
    private const string Scheme = "Bearer ";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (string.IsNullOrEmpty(settings.InternalServiceToken))
        {
            await next();
            return;
        }

        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        if (header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) &&
            FixedTimeEquals(header[Scheme.Length..].Trim(), settings.InternalServiceToken))
        {
            await next();
            return;
        }

        logger.LogWarning(
            "Rejected {Method} {Path}: missing or invalid service credential",
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path);

        context.Result = new UnauthorizedObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "Missing or invalid private service credential.",
        });
    }

    private static bool FixedTimeEquals(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}
