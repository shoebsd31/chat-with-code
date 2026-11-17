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
    /// <summary>
    /// HS256 needs a key of at least 256 bits, and the Marketplace's extension secret is longer than this,
    /// so anything shorter is certainly not the right value (for example a placeholder or a partial copy).
    /// </summary>
    public const int MinimumSecretLength = 32;

    private const string ShortSecretMessage =
        "The backend's extension secret is too short to be the extension's key. Copy the whole key from the " +
        "Marketplace publisher portal (the extension's ⋮ menu > Certificate) into AzureDevOps:ExtensionSecret " +
        "(the 'extension-secret' Key Vault secret in Azure), then restart the API.";

    /// <summary>The configured extension secret, without stray whitespace from copying; null when not set.</summary>
    public static string? ReadSecret(IConfiguration configuration)
    {
        var secret = configuration.GetSection(AzureDevOpsOptions.SectionName).Get<AzureDevOpsOptions>()?.ExtensionSecret?.Trim();
        return string.IsNullOrEmpty(secret) ? null : secret;
    }

    public static IServiceCollection AddExtensionAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var secret = ReadSecret(configuration);
        var configured = secret is not null;
        var tooShort = configured && secret!.Length < MinimumSecretLength;

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
                    // Always say why a token was rejected; the default log level hides it. The message
                    // never contains the token or the secret (IdentityModel hides them).
                    OnAuthenticationFailed = context =>
                    {
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger(typeof(ExtensionAuthentication))
                            .LogWarning("Rejected a request's extension token: {Reason}",
                                tooShort ? ShortSecretMessage : context.Exception.Message);
                        return Task.CompletedTask;
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        var detail = !configured
                            ? "The backend isn't connected to the extension yet. Set AzureDevOps:ExtensionSecret (ADO_EXTENSION_SECRET) to the extension's certificate secret."
                            : tooShort
                                ? ShortSecretMessage
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

    /// <summary>Log a clear warning at startup when the extension secret is missing or obviously wrong.</summary>
    public static void WarnIfExtensionSecretLooksWrong(this WebApplication app)
    {
        var secret = ReadSecret(app.Configuration);
        if (secret is null)
        {
            app.Logger.LogWarning("No extension secret is configured, so every request from the extension is refused. " +
                "Set AzureDevOps:ExtensionSecret (ADO_EXTENSION_SECRET) to the extension's key from the Marketplace.");
        }
        else if (secret.Length < MinimumSecretLength)
        {
            app.Logger.LogWarning("{Message} (It is {Length} characters long.)", ShortSecretMessage, secret.Length);
        }
    }
}
