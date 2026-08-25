using Microsoft.OpenApi.Models;

namespace Dms.Api;

/// <summary>
/// OpenAPI generation, so the frontend is built against a published contract rather than by
/// reading endpoint source.
/// </summary>
public static class OpenApiSetup
{
    public const string EnabledKey = "OpenApi:Enabled";

    public static IServiceCollection AddDmsOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "DMS API",
                Version = "v1",
                Description =
                    "GxP controlled-document management. Every endpoint except /health/*, "
                    + "/api/auth/login and /api/public/editor/* requires a bearer token from "
                    + "POST /api/auth/login.",
            });

            options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Paste the accessToken returned by POST /api/auth/login.",
            });

            // Applied globally rather than per-endpoint because authorization denies by
            // default — the handful of anonymous routes are the exception, and a reader is
            // better served assuming a token is needed than assuming it isn't.
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "bearer" },
                }] = Array.Empty<string>(),
            });

            options.SupportNonNullableReferenceTypes();
        });

        return services;
    }

    /// <summary>
    /// Exposes the spec and UI.
    /// <para>
    /// Off unless <c>OpenApi:Enabled</c> is true. A public, unauthenticated map of every
    /// endpoint in a regulated system is a reconnaissance aid, so switching it on in a
    /// validated environment should be a deliberate act rather than a default someone
    /// inherits.
    /// </para>
    /// </summary>
    public static WebApplication UseDmsOpenApi(this WebApplication app)
    {
        if (!app.Configuration.GetValue(EnabledKey, app.Environment.IsDevelopment()))
        {
            return app;
        }

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "DMS API v1");
            options.DocumentTitle = "DMS API";
        });

        return app;
    }
}
