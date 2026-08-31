using System.Globalization;
using System.Text.RegularExpressions;

namespace FoamWorkbench.Services;

public sealed partial class ResidualParser
{
    private int _sequence;

    public event Action<ResidualSample>? SampleParsed;

    public void ParseLine(string line)
    {
        var match = SolverLine().Match(line);
        if (!match.Success) return;

        if (!double.TryParse(match.Groups["initial"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var initial)) return;
        if (!double.TryParse(match.Groups["final"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var final)) return;
        _ = int.TryParse(match.Groups["iterations"].Value, out var iterations);

        SampleParsed?.Invoke(new ResidualSample
        {
            Field = match.Groups["field"].Value.Trim(),
            Initial = initial,
            Final = final,
            Iterations = iterations,
            Sequence = ++_sequence
        });
    }

    public void Reset(int sequence = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        _sequence = sequence;
    }

    [GeneratedRegex(
        @"Solving for\s+(?<field>[^,]+),\s*Initial residual\s*=\s*(?<initial>[-+0-9.eE]+),\s*Final residual\s*=\s*(?<final>[-+0-9.eE]+),\s*No Iterations\s*(?<iterations>\d+)",
        RegexOptions.Compiled)]
    private static partial Regex SolverLine();
}
