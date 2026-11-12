using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Smartgn.Telemetry.Infrastructure.OpenApi;

public sealed class ServiceAuthOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("serviceAuth")] = [],
        });
    }
}
