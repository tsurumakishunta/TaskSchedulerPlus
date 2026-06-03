using Microsoft.AspNetCore.Identity;

namespace TaskSchedulerPlus.Web.Services;

// Identity が返す標準エラーを、画面表示用の日本語メッセージに差し替えます。
public sealed class JapaneseIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() =>
        Error(nameof(DefaultError), "処理中にエラーが発生しました。");

    public override IdentityError ConcurrencyFailure() =>
        Error(nameof(ConcurrencyFailure), "他の処理により情報が変更されています。画面を再読み込みしてからやり直してください。");

    public override IdentityError PasswordMismatch() =>
        Error(nameof(PasswordMismatch), "現在のパスワードが正しくありません。");

    public override IdentityError InvalidToken() =>
        Error(nameof(InvalidToken), "トークンが無効です。もう一度操作してください。");

    public override IdentityError LoginAlreadyAssociated() =>
        Error(nameof(LoginAlreadyAssociated), "このログイン情報は既に別のユーザーに関連付けられています。");

    public override IdentityError InvalidUserName(string? userName) =>
        Error(nameof(InvalidUserName), $"ユーザー名 '{userName}' は使用できません。");

    public override IdentityError InvalidEmail(string? email) =>
        Error(nameof(InvalidEmail), $"メールアドレス '{email}' の形式が正しくありません。");

    public override IdentityError DuplicateUserName(string userName) =>
        Error(nameof(DuplicateUserName), $"ユーザー名 '{userName}' は既に使用されています。");

    public override IdentityError DuplicateEmail(string email) =>
        Error(nameof(DuplicateEmail), $"メールアドレス '{email}' は既に使用されています。");

    public override IdentityError InvalidRoleName(string? role) =>
        Error(nameof(InvalidRoleName), $"権限 '{role}' は使用できません。");

    public override IdentityError DuplicateRoleName(string role) =>
        Error(nameof(DuplicateRoleName), $"権限 '{role}' は既に存在します。");

    public override IdentityError UserAlreadyHasPassword() =>
        Error(nameof(UserAlreadyHasPassword), "このユーザーには既にパスワードが設定されています。");

    public override IdentityError UserLockoutNotEnabled() =>
        Error(nameof(UserLockoutNotEnabled), "このユーザーではロックアウトが有効になっていません。");

    public override IdentityError UserAlreadyInRole(string role) =>
        Error(nameof(UserAlreadyInRole), $"このユーザーには既に '{role}' 権限が設定されています。");

    public override IdentityError UserNotInRole(string role) =>
        Error(nameof(UserNotInRole), $"このユーザーには '{role}' 権限が設定されていません。");

    public override IdentityError PasswordTooShort(int length) =>
        Error(nameof(PasswordTooShort), $"パスワードは {length} 文字以上で入力してください。");

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        Error(nameof(PasswordRequiresNonAlphanumeric), "パスワードには記号を1文字以上含めてください。");

    public override IdentityError PasswordRequiresDigit() =>
        Error(nameof(PasswordRequiresDigit), "パスワードには数字を1文字以上含めてください。");

    public override IdentityError PasswordRequiresLower() =>
        Error(nameof(PasswordRequiresLower), "パスワードには英小文字を1文字以上含めてください。");

    public override IdentityError PasswordRequiresUpper() =>
        Error(nameof(PasswordRequiresUpper), "パスワードには英大文字を1文字以上含めてください。");

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        Error(nameof(PasswordRequiresUniqueChars), $"パスワードには異なる文字を {uniqueChars} 種類以上含めてください。");

    public override IdentityError RecoveryCodeRedemptionFailed() =>
        Error(nameof(RecoveryCodeRedemptionFailed), "リカバリーコードの確認に失敗しました。");

    // IdentityError の生成形式を揃えるための共通メソッドです。
    private static IdentityError Error(string code, string description) => new()
    {
        Code = code,
        Description = description
    };
}
