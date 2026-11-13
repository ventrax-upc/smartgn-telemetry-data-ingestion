using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Smartgn.Telemetry.Infrastructure.OpenApi;

public sealed class TelemetryTagsDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        document.Tags =
        new HashSet<OpenApiTag>
        {
            new OpenApiTag
            {
                Name = "Internal telemetry",
                Description = "Private service-to-service contracts for device lifecycle and operational status.",
            },
        };
    }
}
