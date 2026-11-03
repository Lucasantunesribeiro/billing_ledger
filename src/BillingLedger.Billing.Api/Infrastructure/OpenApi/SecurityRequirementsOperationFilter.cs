using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BillingLedger.Billing.Api.Infrastructure.OpenApi;

/// <summary>
/// Marca com cadeado (Bearer) somente as operacoes que exigem JWT. O webhook de pagamentos
/// e autenticado por HMAC (header X-Webhook-Signature) e nao deve aparecer como protegido por JWT.
/// </summary>
public sealed class SecurityRequirementsOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var requiresAuth = metadata.OfType<IAuthorizeData>().Any()
                           && !metadata.OfType<IAllowAnonymous>().Any();
        if (!requiresAuth)
            return;

        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Token JWT ausente, invalido ou expirado." });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Papel do token sem permissao para a operacao." });
        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                    },
                    Array.Empty<string>()
                }
            }
        ];
    }
}
