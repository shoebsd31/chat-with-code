using System.Security.Cryptography;
using System.Text;
using ChatWithCode.Core.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ChatWithCode.Api.Auth;

/// <summary>
/// Requests must carry the extension's app token (SDK.getAppToken()) as a bearer token. It is a
/// JWT signed with the extension's secret from the Marketplace publisher portal, so a valid
/// signature proves the request came from our extension. Who the user is, and what they may
/// read, is then confirmed with Azure DevOps (see <see cref="UserAccessService"/>).
/// </summary>
public static class ExtensionAuthentication
{
    public static IServiceCollection AddExtensionAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var secret = configuration.GetSection(AzureDevOpsOptions.SectionName).Get<AzureDevOpsOptions>()?.ExtensionSecret;
        var configured = !string.IsNullOrWhiteSpace(secret);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // Without a secret, use a random key so every token fails instead of the API refusing to start.
                    IssuerSigningKey = new SymmetricSecurityKey(configured ? Encoding.UTF8.GetBytes(secret!) : RandomNumberGenerator.GetBytes(64)),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateActor = false,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                };
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        var detail = !configured
                            ? "The backend isn't connected to the extension yet. Set AzureDevOps:ExtensionSecret (ADO_EXTENSION_SECRET) to the extension's certificate secret."
                            : context.AuthenticateFailure is SecurityTokenExpiredException
                                ? "Your session expired. Reload the page."
                                : "This request didn't come from the Chat with Code extension. Reload the page, and check that the backend uses the current extension secret.";
                        await Results.Problem(detail, statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in")
                            .ExecuteAsync(context.HttpContext);
                    },
                };
            });
        services.AddAuthorization();
        return services;
    }
}
