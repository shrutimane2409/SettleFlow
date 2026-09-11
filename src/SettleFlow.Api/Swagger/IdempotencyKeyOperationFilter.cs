using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

namespace SettleFlow.Api.Swagger;

public sealed class IdempotencyKeyOperationFilter : IOperationFilter
{
    public void Apply(
        OpenApiOperation operation,
        OperationFilterContext context)
    {
        if (context.ApiDescription.HttpMethod != "POST")
            return;

        if (context.ApiDescription.RelativePath != "api/payments")
            return;

        operation.Parameters ??= new List<IOpenApiParameter>();

        operation.Parameters.Add(
            new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = true,
                Description =
                    "Unique key used to safely retry the payment request.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String
                }
            });
    }
}