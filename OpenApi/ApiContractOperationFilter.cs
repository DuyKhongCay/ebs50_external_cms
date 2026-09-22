using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ebs50_backend.OpenApi;

/// <summary>Documents existing controller contracts without changing runtime formatting or validation.</summary>
public sealed class ApiContractOperationFilter : IOperationFilter
{
    /// <summary>Adds stable operation IDs, validation alternatives and binary response media types.</summary>
    /// <param name="operation">Generated operation to document.</param>
    /// <param name="context">Controller action metadata and schema generator.</param>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor is not ControllerActionDescriptor action)
        {
            return;
        }

        operation.OperationId = $"{action.ControllerName}_{context.MethodInfo.Name}";

        if (action.ControllerName == "Database")
        {
            operation.Description = (operation.Description ?? "") + " Localhost only; remote/proxied access is rejected.";
            if (context.ApiDescription.HttpMethod == "POST")
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "X-CSRF-TOKEN", In = ParameterLocation.Header, Required = true,
                    Description = "Antiforgery token from /Database, together with its cookie. Reload the page after restarting the server.",
                    Schema = new OpenApiSchema { Type = "string" }
                });
                operation.Responses.TryAdd("400", new OpenApiResponse { Description = "Invalid request or missing/invalid antiforgery token." });
            }
            if (context.MethodInfo.Name == "Download")
            {
                operation.Responses["200"] = new OpenApiResponse
                {
                    Description = "SQLite snapshot and manifest in a ZIP archive.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/zip"] = new() { Schema = new OpenApiSchema { Type = "string", Format = "binary" } }
                    }
                };
            }
        }

        // ApiController short-circuits invalid binding/validation before the action runs.
        // Business errors still use the action's declared response type.
        bool hasBoundParameters = context.ApiDescription.ParameterDescriptions
            .Any(p => p.Source?.IsFromRequest == true);
        if (hasBoundParameters)
        {
            var validationSchema = context.SchemaGenerator.GenerateSchema(typeof(ValidationProblemDetails), context.SchemaRepository);
            var declaredType = context.ApiDescription.SupportedResponseTypes
                .FirstOrDefault(r => r.StatusCode == 400)?.Type;
            var jsonSchema = validationSchema;
            if (declaredType != null && declaredType != typeof(void)
                && declaredType != typeof(ProblemDetails) && declaredType != typeof(ValidationProblemDetails))
            {
                jsonSchema = new OpenApiSchema
                {
                    // These existing DTO schemas have optional members, so they can overlap.
                    // anyOf accepts either runtime shape without claiming mutual exclusivity.
                    AnyOf = new List<OpenApiSchema>
                    {
                        context.SchemaGenerator.GenerateSchema(declaredType, context.SchemaRepository),
                        validationSchema
                    }
                };
            }

            var response = new OpenApiResponse
            {
                Description = "Invalid binding/validation (ValidationProblemDetails), or the documented business error.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = jsonSchema },
                    ["application/problem+json"] = new() { Schema = validationSchema }
                }
            };
            if (declaredType == typeof(string))
            {
                response.Content["text/plain"] = new() { Schema = new OpenApiSchema { Type = "string" } };
            }
            operation.Responses["400"] = response;
        }

        if (context.ApiDescription.ParameterDescriptions.Any(p => p.Source?.Id == "Body"))
        {
            operation.Responses.TryAdd("415", new OpenApiResponse
            {
                Description = "Unsupported request Content-Type.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new()
                    {
                        Schema = context.SchemaGenerator.GenerateSchema(typeof(ProblemDetails), context.SchemaRepository)
                    }
                }
            });
        }

        string[]? imageTypes = action.ControllerName switch
        {
            "Render" => ["image/png"],
            "States" when context.MethodInfo.Name == "GetStateIcon" => ["image/png", "image/bmp", "image/jpeg"],
            _ => null
        };
        if (imageTypes != null)
        {
            operation.Responses["200"] = new OpenApiResponse
            {
                Description = "Image bytes; a preview is not a transmission to a tag.",
                Content = imageTypes.ToDictionary(t => t, _ => new OpenApiMediaType
                {
                    Schema = new OpenApiSchema { Type = "string", Format = "binary" }
                })
            };
        }
    }
}
