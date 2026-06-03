using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TaskSchedulerPlus.Web.Data;

namespace Microsoft.AspNetCore.Routing;

// Identity の Razor コンポーネントで必要な追加エンドポイントを登録します。
internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    // /Components/Account/Pages 配下の Identity 画面が利用するエンドポイントです。
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/Logout", async (
            ClaimsPrincipal user,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            // returnUrl が未指定ならログイン画面に戻します。
            return TypedResults.LocalRedirect($"~/{NormalizeLogoutReturnUrl(returnUrl)}");
        });

        var manageGroup = accountGroup.MapGroup("/Manage").RequireAuthorization();

        var loggerFactory = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var downloadLogger = loggerFactory.CreateLogger("DownloadPersonalData");

        manageGroup.MapPost("/DownloadPersonalData", async (
            HttpContext context,
            [FromServices] UserManager<ApplicationUser> userManager,
            [FromServices] AuthenticationStateProvider authenticationStateProvider) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
            {
                return Results.NotFound($"ユーザーID '{userManager.GetUserId(context.User)}' の情報を読み込めませんでした。");
            }

            var userId = await userManager.GetUserIdAsync(user);
            downloadLogger.LogInformation("ユーザーID '{UserId}' が個人データのダウンロードを要求しました。", userId);

            // PersonalData 属性が付いたプロパティだけをダウンロード対象にします。
            var personalData = new Dictionary<string, string>();
            var personalDataProps = typeof(ApplicationUser).GetProperties().Where(
                prop => Attribute.IsDefined(prop, typeof(PersonalDataAttribute)));
            foreach (var p in personalDataProps)
            {
                personalData.Add(p.Name, p.GetValue(user)?.ToString() ?? "null");
            }

            personalData.Add("認証キー", (await userManager.GetAuthenticatorKeyAsync(user))!);
            var fileBytes = JsonSerializer.SerializeToUtf8Bytes(personalData);

            context.Response.Headers.TryAdd("Content-Disposition", "attachment; filename=PersonalData.json");
            return TypedResults.File(fileBytes, contentType: "application/json", fileDownloadName: "PersonalData.json");
        });

        return accountGroup;
    }

    private static string NormalizeLogoutReturnUrl(string? returnUrl) =>
        string.IsNullOrWhiteSpace(returnUrl) ? "Account/Login" : returnUrl.TrimStart('/');
}
