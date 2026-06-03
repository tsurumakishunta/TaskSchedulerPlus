using Microsoft.AspNetCore.Identity;

namespace TaskSchedulerPlus.Web.Data;

// Identity 標準ユーザーをアプリ側で拡張するための型です。
// 将来プロフィール項目を増やす場合は、このクラスにプロパティを追加します。
public class ApplicationUser : IdentityUser
{
}

