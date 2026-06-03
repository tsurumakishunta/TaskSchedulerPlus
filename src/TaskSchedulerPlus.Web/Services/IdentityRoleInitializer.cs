using TaskSchedulerPlus.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace TaskSchedulerPlus.Web.Services;

// アプリで必要な Identity ロールを DB に作成します。
public static class IdentityRoleInitializer
{
    public static async Task EnsureRolesAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in AppRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                // ロールが欠けている場合だけ作成し、起動時に何度呼ばれても安全にします。
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        string.Join(" / ", result.Errors.Select(error => error.Description)));
                }
            }
        }
    }
}
