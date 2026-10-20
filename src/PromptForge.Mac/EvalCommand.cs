using System.Text.Json;
using PromptForge.Core.Eval;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;

namespace PromptForge.Mac;

/// <summary>
/// Runs the optimiser over a test set with the saved settings and API keys, writing report.md and
/// results.json. Available as
/// "PromptForge.app/Contents/MacOS/PromptForge --eval cases.json outDir [--judge] [--repeat N] [--system-prompt file] [--pause-seconds N]"
/// and from the menu bar (Developer → Run quality check). Keys never leave this Mac.
/// "PromptForge --eval-compare before/results.json after/results.json compare.md" compares two runs.
/// </summary>
internal static class EvalCommand
{
    public const int DefaultRepeat = EvalRunner.DefaultRepeat;

    private const string Usage =
        "Usage:\nPromptForge.app/Contents/MacOS/PromptForge --eval <cases.json> <output folder> [--judge] [--repeat N] [--system-prompt <file>] [--pause-seconds N] [--include-held-out]\n" +
        "PromptForge.app/Contents/MacOS/PromptForge --eval-compare <before results.json> <after results.json> <output .md>";

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var pause = TimeSpan.FromSeconds(Option(args, "--pause-seconds") is { } p && double.TryParse(p, out var s) ? s : 8);
        var repeat = Option(args, "--repeat") is { } r && int.TryParse(r, out var n) && n > 0 ? n : DefaultRepeat;
        var instructions = Option(args, "--system-prompt");
        using var services = new AppServices();
        return Task.Run(() => RunAsync(services, args[0], args[1], args.Contains("--judge"), pause, repeat, instructions, args.Contains("--include-held-out"))).GetAwaiter().GetResult() ? 0 : 1;
    }

    public static int Compare(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var output = Path.GetFullPath(args[2]);
        if (output.Equals(Path.GetFullPath(args[0]), StringComparison.OrdinalIgnoreCase) ||
            output.Equals(Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The comparison would overwrite one of the runs. Choose a different output file.");
            return 2;
        }
        try
        {
            var report = EvalComparison.Build(EvalComparison.LoadRuns(args[0]), EvalComparison.LoadRuns(args[1]), args[0], args[1]);
            File.WriteAllText(args[2], report);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Couldn't compare the runs: " + ex.Message);
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// The system prompt from a file, e.g. eval/prompts/v1.0.0.txt, to measure it against the built-in one.
    /// Line endings are normalised so a file saved on Windows matches the built-in text byte for byte.
    /// </summary>
    public static string LoadInstructions(string path) => File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>Returns true on success. Progress and the final summary go to progress.txt in the output folder.</summary>
    public static async Task<bool> RunAsync(AppServices services, string casesPath, string outDir, bool judge, TimeSpan pause,
        int repeat = DefaultRepeat, string? instructionsPath = null, bool includeHeldOut = false)
    {
        Directory.CreateDirectory(outDir);
        var progressFile = Path.Combine(outDir, "progress.txt");
        void Progress(string msg) => File.WriteAllText(progressFile, $"{DateTime.Now:HH:mm:ss} {msg}");
        try
        {
            var allCases = EvalRunner.LoadCases(casesPath);
            var cases = EvalRunner.SelectCases(allCases, includeHeldOut);
            var endpoints = EndpointResolver.Resolve(services.Settings, services.Secrets);
            var (judgeEndpoint, independent) = EndpointResolver.ResolveJudge(services.Settings, services.Secrets, endpoints);
            var options = instructionsPath is null ? new OptimizerOptions() : new OptimizerOptions { Instructions = LoadInstructions(instructionsPath) };
            var info = new EvalRunInfo(endpoints[0].Label, judge ? judgeEndpoint.Label : null, independent,
                instructionsPath is null ? null : Path.GetFileName(instructionsPath), HeldOutSkipped: allCases.Count - cases.Count);
            var runner = new EvalRunner(services.Optimizer, services.Client);
            Log.Info($"eval started cases={cases.Count} repeat={repeat} judge={(judge ? judgeEndpoint.Label : "off")} independent={independent} customPrompt={instructionsPath is not null}");
            var results = await runner.RunAsync(cases, endpoints, judge ? judgeEndpoint : null, pause, new Progress<string>(Progress), CancellationToken.None, repeat, options);

            info = info with { StoppedEarly = runner.StoppedEarly };
            await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), EvalRunner.BuildReport(results, info));
            await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results.Select(r => new
            {
                r.Case.Id,
                r.Case.Category,
                r.Run,
                r.Passed,
                r.Error,
                r.Failures,
                Output = r.Result?.Prompt,
                Model = r.Result?.Model,
                TaskType = r.Result?.TaskType.ToString(),
                Verdict = r.Result?.Verdict.ToString(),
                r.Result?.Questions,
                r.Result?.Warnings,
                Ms = r.Result?.Elapsed.TotalMilliseconds,
                r.Result?.PromptTokens,
                r.Result?.CachedTokens,
                r.Result?.CompletionTokens,
                r.Judge,
            }), new JsonSerializerOptions { WriteIndented = true }));
            var errors = results.Count(r => r.Error is not null);
            var summary = $"{(runner.StoppedEarly is null ? "done" : "stopped early")}: {results.Count(r => r.Passed)}/{results.Count - errors} runs passed, {errors} errored"
                + (runner.StoppedEarly is null ? "" : $". {runner.StoppedEarly}");
            Progress(summary);
            Log.Info("eval " + summary);
            return true;
        }
        catch (Exception ex)
        {
            Progress($"failed: {ex.GetType().Name}: {ex.Message}");
            Log.Error("eval failed", ex);
            return false;
        }
    }
}
