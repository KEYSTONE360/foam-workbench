using System.Diagnostics;
using System.Text;

namespace FoamWorkbench.Services;

public sealed class ProcessOutputEventArgs(string line, bool isError) : EventArgs
{
    public string Line { get; } = line;
    public bool IsError { get; } = isError;
}

public sealed record ProcessResult(int ExitCode, TimeSpan Duration, string Output);

public sealed class ProcessRunner
{
    private const int MaximumRetainedOutputCharacters = 16_000_000;
    private Process? _activeProcess;
    private CancellationTokenSource? _activeCancellation;

    public bool IsRunning => _activeProcess is { HasExited: false };
    public event EventHandler<ProcessOutputEventArgs>? OutputReceived;

    public async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        if (IsRunning) throw new InvalidOperationException("다른 OpenFOAM 작업이 이미 실행 중입니다.");

        var started = DateTimeOffset.Now;
        var output = new StringBuilder();
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _activeProcess = process;
        process.OutputDataReceived += (_, e) => Receive(e.Data, false, output);
        process.ErrorDataReceived += (_, e) => Receive(e.Data, true, output);

        try
        {
            if (!process.Start()) throw new InvalidOperationException($"{startInfo.FileName}을 시작할 수 없습니다.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(_activeCancellation.Token);
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, DateTimeOffset.Now - started, output.ToString());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            process.Dispose();
            _activeProcess = null;
            _activeCancellation.Dispose();
            _activeCancellation = null;
        }
    }

    public void Cancel()
    {
        _activeCancellation?.Cancel();
        if (_activeProcess is not null) TryKill(_activeProcess);
    }

    private void Receive(string? line, bool isError, StringBuilder output)
    {
        if (line is null) return;
        lock (output)
        {
            output.AppendLine(line);
            if (output.Length > MaximumRetainedOutputCharacters + 1_000_000)
            {
                output.Remove(0, output.Length - MaximumRetainedOutputCharacters);
                output.Insert(0, "[FoamWorkbench: earlier live output omitted; retained tail follows]" + Environment.NewLine);
            }
        }
        OutputReceived?.Invoke(this, new ProcessOutputEventArgs(line, isError));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // A process can exit between HasExited and Kill.
        }
    }
}
