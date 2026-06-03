using System.Net;
using System.Net.Mail;
using TaskSchedulerPlus.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace TaskSchedulerPlus.Web.Services;

// ASP.NET Core Identity から呼ばれるメール送信処理です。
// 実際の SMTP 設定は mail.json から読み込みます。
public sealed class ParameterEmailSender(
    ParameterFileStore parameterFileStore,
    ILogger<ParameterEmailSender> logger) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendIdentityEmailAsync(email, "メールアドレスの確認", $"<a href='{confirmationLink}'>こちら</a>からアカウントを確認してください。");

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendIdentityEmailAsync(email, "パスワード再設定", $"<a href='{resetLink}'>こちら</a>からパスワードを再設定してください。");

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SendIdentityEmailAsync(email, "パスワード再設定", $"次のコードを使用してパスワードを再設定してください: {resetCode}");

    public Task<ParameterEmailSendResult> SendTestEmailAsync(string email)
    {
        var sentAt = DateTimeOffset.Now.ToString("yyyy/MM/dd HH:mm:ss zzz");
        var body = $"""
            <p>これはワークフロー運用管理から送信されたテストメールです。</p>
            <p>このメールを受信できていれば、mail.json のSMTP設定でメール送信が可能です。</p>
            <p>送信日時: {WebUtility.HtmlEncode(sentAt)}</p>
            """;

        return SendEmailAsync(email, "メール送信テスト", body);
    }

    private async Task SendIdentityEmailAsync(string email, string subject, string htmlBody)
    {
        _ = await SendEmailAsync(email, subject, htmlBody);
    }

    private async Task<ParameterEmailSendResult> SendEmailAsync(string email, string subject, string htmlBody)
    {
        var settings = parameterFileStore.ReadMailParameter();
        var recipientDomain = ToDomain(email);

        if (!settings.IsConfigured())
        {
            // メール未設定の環境では送信せず、ログに理由を残して処理を継続します。
            logger.LogWarning(
                "メール送信設定が未設定のため、メール送信をスキップしました。RecipientDomain={RecipientDomain}, Subject={Subject}",
                recipientDomain,
                subject);
            return new ParameterEmailSendResult(false, "メール送信設定が未設定です。mail.json の内容を確認してください。");
        }

        try
        {
            logger.LogInformation(
                "メール送信を開始しました。RecipientDomain={RecipientDomain}, Subject={Subject}, SmtpHost={SmtpHost}, SmtpPort={SmtpPort}, EnableSsl={EnableSsl}",
                recipientDomain,
                subject,
                settings.SmtpHost,
                settings.SmtpPort,
                settings.EnableSsl);

            using var message = new MailMessage
            {
                From = new MailAddress(settings.SenderAddress, settings.SenderName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(email);

            using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = settings.EnableSsl,
                Timeout = Math.Max(1, settings.TimeoutSeconds) * 1000
            };

            if (!string.IsNullOrWhiteSpace(settings.UserName))
            {
                // SMTP 認証情報が設定されている場合だけ資格情報を付与します。
                client.Credentials = new NetworkCredential(settings.UserName, settings.Password);
            }

            await client.SendMailAsync(message);
            logger.LogInformation(
                "メール送信を終了しました。RecipientDomain={RecipientDomain}, Subject={Subject}",
                recipientDomain,
                subject);
            return new ParameterEmailSendResult(true, $"テストメールを {email} に送信しました。");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "メール送信に失敗しました。RecipientDomain={RecipientDomain}, Subject={Subject}",
                recipientDomain,
                subject);
            return new ParameterEmailSendResult(false, $"メール送信に失敗しました。SMTP設定、認証情報、送信先メールアドレスを確認してください。詳細: {exception.Message}");
        }
    }

    private static string ToDomain(string email)
    {
        var atIndex = email.LastIndexOf('@');
        return atIndex >= 0 && atIndex < email.Length - 1 ? email[(atIndex + 1)..] : "unknown";
    }
}

public sealed record ParameterEmailSendResult(bool Succeeded, string Message);
