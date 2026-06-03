using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using TaskSchedulerPlus.Web.Data;

namespace TaskSchedulerPlus.Web.Components.Account;

// Identity UI の既定実装に合わせるためのダミー送信クラスです。
// 実運用のメール送信は Program.cs で ParameterEmailSender に差し替えています。
internal sealed class IdentityNoOpEmailSender : IEmailSender<ApplicationUser>
{
    private readonly IEmailSender emailSender = new NoOpEmailSender();

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        emailSender.SendEmailAsync(email, "メールアドレスの確認", $"<a href='{confirmationLink}'>こちら</a>からアカウントを確認してください。");
 
    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        emailSender.SendEmailAsync(email, "パスワード再設定", $"<a href='{resetLink}'>こちら</a>からパスワードを再設定してください。");
 
    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        emailSender.SendEmailAsync(email, "パスワード再設定", $"次のコードを使用してパスワードを再設定してください: {resetCode}");
}
