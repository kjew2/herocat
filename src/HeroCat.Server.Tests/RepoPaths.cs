namespace HeroCat.Server.Tests;

/// <summary>Finds files of the repository from the test's output directory.</summary>
static class RepoPaths
{
    public static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "HeroCat.sln"))) return dir.FullName;
            }
            throw new InvalidOperationException("HeroCat.sln not found above " + AppContext.BaseDirectory);
        }
    }

    public static string Of(string relative) => Path.Combine(Root, relative);
}
