using Microsoft.AspNetCore.Identity;
using TaskSchedulerPlus.Web.Data;

namespace TaskSchedulerPlus.Web.Components.Account;

// Identity 管理画面で、現在ログイン中のユーザーを必ず取得するためのヘルパーです。
internal sealed class IdentityUserAccessor(UserManager<ApplicationUser> userManager, IdentityRedirectManager redirectManager)
{
    public async Task<ApplicationUser> GetRequiredUserAsync(HttpContext context)
    {
        var user = await userManager.GetUserAsync(context.User);

        if (user is null)
        {
            // Cookie はあるがユーザー実体が取得できない場合は、不正ユーザー画面へ誘導します。
            redirectManager.RedirectToWithStatus("Account/InvalidUser", $"エラー: ユーザーID '{userManager.GetUserId(context.User)}' の情報を読み込めませんでした。", context);
        }

        return user;
    }
}
