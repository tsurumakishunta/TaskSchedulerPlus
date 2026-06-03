namespace TaskSchedulerPlus.Web.Services;

// プロジェクト配下から .sln が置かれているソースルートを見つけるためのヘルパーです。
public static class SolutionPathResolver
{
    public static string ResolveSolutionRoot(string contentRootPath)
    {
        var directory = new DirectoryInfo(contentRootPath);
        while (directory is not null)
        {
            // .sln ファイルが見つかったディレクトリをソースルートとみなします。
            if (directory.EnumerateFiles("*.sln").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return contentRootPath;
    }
}
