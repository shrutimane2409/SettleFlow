using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SettleFlow.Api.Swagger;

/// <summary>
/// Makes generated clients exact:
/// <list type="bullet">
/// <item>Non-nullable value types (decimal, Guid, enums, DateTimeOffset…) are always present, so they're required.</item>
/// <item>Response DTOs (<c>*Dto</c>, <c>PagedResult</c>) always serialize every property, including nulls, so every
/// property is required (nullable ones as <c>T | null</c>). Request types keep optional fields.</item>
/// </list>
/// </summary>
public sealed class RequiredValueTypesSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema s || s.Properties is null || context.Type is null) return;
        var isResponseDto = context.Type.Name.EndsWith("Dto") || context.Type.Name.StartsWith("PagedResult");
        foreach (var prop in context.Type.GetProperties())
        {
            var t = prop.PropertyType;
            if (!isResponseDto && (!t.IsValueType || Nullable.GetUnderlyingType(t) is not null)) continue;
            var name = s.Properties.Keys.FirstOrDefault(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase));
            if (name is null) continue;
            s.Required ??= new HashSet<string>();
            s.Required.Add(name);
        }
    }
}
