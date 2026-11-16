using ChatWithCode.Api.Auth;
using ChatWithCode.Api.Chat;

namespace ChatWithCode.Api.Admin;

/// <summary>
/// Settings and repository management. Anyone signed in can read settings (secrets are never
/// returned); changing anything requires being listed in CHAT_ADMINS.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin")
            .RequireAuthorization()
            .AddEndpointFilter<RequireUserAccessFilter>()
            .AddEndpointFilter(async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (ChatRequestException ex)
                {
                    return Results.Problem(ex.Message, statusCode: ex.StatusCode, title: ex.StatusCode == 403 ? "Not allowed" : "Can't do that");
                }
            });

        admin.MapGet("/settings", (Guid? projectId, HttpContext http, AdminService service, CancellationToken ct) =>
            service.GetSettingsAsync(http.GetUserAccess(), projectId, ct));

        admin.MapPut("/settings", (SaveSettingsRequest request, HttpContext http, AdminService service, CancellationToken ct) =>
            service.SaveSettingsAsync(http.GetUserAccess(), null, request, ct));

        admin.MapPut("/projects/{projectId:guid}/settings", (Guid projectId, SaveSettingsRequest request, HttpContext http, AdminService service, CancellationToken ct) =>
            service.SaveSettingsAsync(http.GetUserAccess(), projectId, request, ct));

        admin.MapDelete("/projects/{projectId:guid}/settings", async (Guid projectId, HttpContext http, AdminService service, CancellationToken ct) =>
        {
            await service.DeleteProjectSettingsAsync(http.GetUserAccess(), projectId, ct);
            return Results.NoContent();
        });

        admin.MapPost("/settings/test", (TestSettingsRequest request, HttpContext http, AdminService service, CancellationToken ct) =>
            service.TestSettingsAsync(http.GetUserAccess(), request.ProjectId, ct));

        admin.MapGet("/projects/{projectId:guid}/repositories", (Guid projectId, HttpContext http, AdminService service, CancellationToken ct) =>
            service.GetRepositoriesAsync(http.GetUserAccess(), projectId, ct));

        admin.MapGet("/repositories/{repositoryId:guid}/branches", (Guid repositoryId, HttpContext http, AdminService service, CancellationToken ct) =>
            service.GetBranchesAsync(http.GetUserAccess(), repositoryId, ct));

        admin.MapPut("/repositories/{repositoryId:guid}/branches", (Guid repositoryId, SetBranchesRequest request, HttpContext http, AdminService service, CancellationToken ct) =>
            service.SetBranchesAsync(http.GetUserAccess(), repositoryId, request, ct));

        admin.MapPost("/repositories/{repositoryId:guid}/reindex", async (Guid repositoryId, ReindexRequest request, HttpContext http, AdminService service, CancellationToken ct) =>
        {
            await service.ReindexAsync(http.GetUserAccess(), repositoryId, request, ct);
            return Results.Accepted();
        });

        return app;
    }
}
