using Microsoft.AspNetCore.Mvc.Filters;

namespace Smartgn.Telemetry.Interfaces.Rest;

/// <summary>
/// Carries the caller <c>X-Correlation-ID</c> into the log scope, matching the
/// trace contract used by Installation and the API Gateway.
/// </summary>
public sealed class CorrelationIdFilter(ILogger<CorrelationIdFilter> logger) : IAsyncActionFilter
{
    private const string HeaderName = "X-Correlation-ID";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var correlationId = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            await next();
            return;
        }

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next();
        }
    }
}
