using System.IO;
using System.Text.RegularExpressions;

namespace FoamWorkbench.Services;

public static partial class CaseInspector
{
    public static CaseInfo Inspect(string path)
    {
        var controlDict = Path.Combine(path, "system", "controlDict");
        var text = File.Exists(controlDict) ? File.ReadAllText(controlDict) : "";
        return new CaseInfo
        {
            Path = path,
            HasZero = Directory.Exists(Path.Combine(path, "0")) ||
                      Directory.Exists(Path.Combine(path, "0.orig")),
            HasConstant = Directory.Exists(Path.Combine(path, "constant")),
            HasSystem = Directory.Exists(Path.Combine(path, "system")),
            Application = MatchEntry(text, "application"),
            SolverModule = MatchEntry(text, "solver")
        };
    }

    public static string MatchEntry(string dictionary, string key)
    {
        var withoutComments = BlockComment().Replace(LineComment().Replace(dictionary, ""), "");
        var match = Regex.Match(withoutComments, $@"(?m)^\s*{Regex.Escape(key)}\s+([^;]+);");
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    [GeneratedRegex(@"//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();
}
