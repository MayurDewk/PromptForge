using System.Net;
using PromptForge.Core.Diffing;
using PromptForge.Core.Eval;
using PromptForge.Core.Llm;
using PromptForge.Core.Optimization;
using PromptForge.Core.Settings;

namespace PromptForge.Core.Tests;

public class ErrorMappingTests
{
    private static LlmException Map(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("retry-after", retryAfter);
        return OpenAiCompatibleClient.MapError(response, body);
    }

    [Fact]
    public void Groq_tpm_limit_parses_wait_time()
    {
        var ex = Map(HttpStatusCode.TooManyRequests,
            """{"error":{"message":"Rate limit reached for model `openai/gpt-oss-120b` on tokens per minute (TPM): Limit 8000, Used 7000, Requested 2000. Please try again in 7.5s."}}""");
        Assert.Equal(LlmErrorKind.RateLimit, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(7.5), ex.RetryAfter);
    }

    [Fact]
    public void Request_larger_than_the_whole_limit_is_too_large_not_rate_limited()
    {
        var ex = Map(HttpStatusCode.TooManyRequests,
            """{"error":{"message":"Request too large for model on tokens per minute (TPM): Limit 8000, Requested 12000."}}""");
        Assert.Equal(LlmErrorKind.TooLarge, ex.Kind);
    }

    [Theory]
    [InlineData("450ms", 0.45)]
    [InlineData("1m3s", 63)]
    [InlineData("2s", 2)]
    public void Try_again_formats(string text, double seconds)
    {
        var ex = Map(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"Please try again in " + text + ".\"}}");
        Assert.Equal(seconds, ex.RetryAfter!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void Retry_after_header_wins()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), Map(HttpStatusCode.TooManyRequests, "{}", "12").RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Invalid API Key\"}}", LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"The model `x` does not exist\"}}", LlmErrorKind.ModelNotFound)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"response_format json_object is not supported\"}}", LlmErrorKind.JsonModeUnsupported)]
    [InlineData(HttpStatusCode.BadRequest, "[{\"error\":{\"message\":\"API key not valid. Please pass a valid API key.\"}}]", LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "upstream down", LlmErrorKind.Server)]
    public void Status_codes_map_to_kinds(HttpStatusCode status, string body, LlmErrorKind kind) =>
        Assert.Equal(kind, Map(status, body).Kind);
}

public class HotkeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Alt+P", true, true, false, "P")]
    [InlineData("ctrl + shift + f8", true, false, true, "f8")]
    [InlineData("Alt+1", false, true, false, "D1")]
    public void Parses(string text, bool ctrl, bool alt, bool shift, string key)
    {
        Assert.True(HotkeyChord.TryParse(text, out var chord));
        Assert.Equal((ctrl, alt, shift, key), (chord.Ctrl, chord.Alt, chord.Shift, chord.Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    public void Rejects_invalid(string text) => Assert.False(HotkeyChord.TryParse(text, out _));

    [Fact]
    public void Requires_ctrl_or_alt()
    {
        HotkeyChord.TryParse("Shift+P", out var chord);
        Assert.NotNull(chord.Problem());
        HotkeyChord.TryParse("Ctrl+Alt+Space", out chord);
        Assert.Null(chord.Problem());
    }

    [Fact]
    public void Round_trips_to_display_text()
    {
        HotkeyChord.TryParse("alt+ctrl+1", out var chord);
        Assert.Equal("Ctrl+Alt+1", chord.ToString());
    }
}

public class TextDiffTests
{
    [Fact]
    public void Word_level_segments_reconstruct_both_texts()
    {
        var a = "fix my login page it doesnt work";
        var b = "Fix my login page. It doesn't work after adding JWT.";
        var segments = TextDiff.Compute(a, b);

        Assert.Equal(a, string.Concat(segments.Where(s => s.Kind != DiffKind.Inserted).Select(s => s.Text)));
        Assert.Equal(b, string.Concat(segments.Where(s => s.Kind != DiffKind.Deleted).Select(s => s.Text)));
        Assert.Contains(segments, s => s.Kind == DiffKind.Inserted && s.Text.Contains("JWT"));
    }

    [Fact]
    public void Identical_text_is_one_equal_segment()
    {
        var segments = TextDiff.Compute("same text", "same text");
        Assert.Single(segments);
        Assert.Equal(0, TextDiff.ChangedFraction(segments));
    }

    [Fact]
    public void Handles_several_thousand_words()
    {
        var words = Enumerable.Range(0, 4000).Select(i => "word" + i);
        var a = string.Join(" ", words);
        var b = a.Replace("word2000", "changed");
        var segments = TextDiff.Compute(a, b);
        Assert.Contains(segments, s => s.Kind == DiffKind.Inserted && s.Text == "changed");
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_and_defaults_to_groq()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var settings = new AppSettings { DefaultMode = OptimizeMode.Light, PersonalContext = "CS student" };
            SettingsStore.Save(path, settings);
            var loaded = SettingsStore.Load(path);
            Assert.Equal(OptimizeMode.Light, loaded.DefaultMode);
            Assert.Equal("CS student", loaded.PersonalContext);
            Assert.Equal("groq", loaded.Primary.Provider);
            Assert.Contains("\"Light\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_is_kept_aside()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var loaded = SettingsStore.Load(path);
            Assert.Equal(OptimizeMode.Standard, loaded.DefaultMode);
            Assert.True(File.Exists(path + ".bad"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bad");
        }
    }

    [Fact]
    public void Resolver_orders_endpoints_and_requires_keys()
    {
        var secrets = new MemorySecrets();
        var settings = new AppSettings();
        Assert.Throws<MissingKeyException>(() => EndpointResolver.Resolve(settings, secrets));

        secrets.Set("groq", "gsk_test");
        var endpoints = EndpointResolver.Resolve(settings, secrets);
        Assert.Equal(["openai/gpt-oss-120b", "openai/gpt-oss-20b"], endpoints.Select(e => e.Model));
        Assert.All(endpoints, e => Assert.Equal("https://api.groq.com/openai/v1", e.BaseUrl));
    }

    [Fact]
    public void Ollama_needs_no_key_and_custom_uses_its_own_url()
    {
        var secrets = new MemorySecrets();
        var settings = new AppSettings
        {
            Primary = new EndpointSettings { Provider = "ollama", Model = "qwen3:4b" },
            Fallback = new EndpointSettings { Provider = "custom", Model = "m", BaseUrl = "https://example.com/v1/" },
        };
        secrets.Set("custom", "abc");
        var endpoints = EndpointResolver.Resolve(settings, secrets);
        Assert.Equal("http://localhost:11434/v1", endpoints[0].BaseUrl);
        Assert.Equal("https://example.com/v1/", endpoints[1].BaseUrl);
    }

    [Theory]
    [InlineData("http://example.com/v1", true)]       // key would travel unencrypted
    [InlineData("ftp://example.com/v1", true)]
    [InlineData("https://user:pw@example.com/v1", true)]
    [InlineData("not a url", true)]
    [InlineData("http://localhost:1234/v1", false)]   // LM Studio on this machine
    [InlineData("http://127.0.0.1:8080/v1", false)]
    [InlineData("https://example.com/v1", false)]
    public void Custom_urls_that_would_leak_the_key_are_refused(string url, bool refused)
    {
        var secrets = new MemorySecrets();
        secrets.Set("custom", "abc");
        var settings = new AppSettings
        {
            Primary = new EndpointSettings { Provider = "custom", Model = "m", BaseUrl = url },
            Fallback = new EndpointSettings { Enabled = false },
        };
        if (refused) Assert.Throws<InsecureEndpointException>(() => EndpointResolver.Resolve(settings, secrets));
        else Assert.Single(EndpointResolver.Resolve(settings, secrets));
    }

    [Fact]
    public void Keyless_local_network_ollama_is_allowed_over_http()
    {
        var settings = new AppSettings
        {
            Primary = new EndpointSettings { Provider = "ollama", Model = "qwen3:4b", BaseUrl = "http://192.168.1.20:11434/v1" },
            Fallback = new EndpointSettings { Enabled = false },
        };
        Assert.Single(EndpointResolver.Resolve(settings, new MemorySecrets()));
    }

    [Fact]
    public void Judge_uses_its_own_provider_when_the_key_is_saved()
    {
        var secrets = new MemorySecrets();
        secrets.Set("groq", "gsk_test");
        var settings = new AppSettings();
        var optimiser = EndpointResolver.Resolve(settings, secrets);

        var (fallbackJudge, independent) = EndpointResolver.ResolveJudge(settings, secrets, optimiser);
        Assert.Equal("openai/gpt-oss-20b", fallbackJudge.Model);
        Assert.False(independent);

        secrets.Set("gemini", "g_test");
        var (judge, independentNow) = EndpointResolver.ResolveJudge(settings, secrets, optimiser);
        Assert.Equal(ProviderPresets.Gemini.DefaultModel, judge.Model);
        Assert.True(independentNow);
    }

    [Fact]
    public void Judge_on_the_optimisers_provider_is_not_independent()
    {
        var secrets = new MemorySecrets();
        secrets.Set("groq", "gsk_test");
        var settings = new AppSettings { Judge = new EndpointSettings { Provider = "groq", Model = "qwen/qwen3-32b" } };
        var (judge, independent) = EndpointResolver.ResolveJudge(settings, secrets, EndpointResolver.Resolve(settings, secrets));
        Assert.Equal("qwen/qwen3-32b", judge.Model);
        Assert.False(independent);
    }

    [Fact]
    public void Settings_saved_before_the_judge_existed_get_the_default_judge()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"version":1}""", SettingsStore.JsonOptions)!;
        Assert.Equal("gemini", settings.Judge.Provider);
    }

    [Fact]
    public void A_null_judge_in_the_settings_file_gets_the_default()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"version":1,"judge":null}""");
            Assert.Equal("gemini", SettingsStore.Load(path).Judge.Provider);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Same_hosted_provider_at_another_url_is_not_independent()
    {
        var secrets = new MemorySecrets();
        secrets.Set("groq", "gsk_test");
        var settings = new AppSettings { Judge = new EndpointSettings { Provider = "groq", Model = "qwen/qwen3-32b" } };
        var optimiser = new[] { new LlmEndpoint(ProviderPresets.Groq, "https://api.groq.com/openai/v1/", "openai/gpt-oss-120b", "k") };
        Assert.False(EndpointResolver.ResolveJudge(settings, secrets, optimiser).Independent);
    }

    internal sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string providerId) => _values.GetValueOrDefault(providerId);
        public void Set(string providerId, string? value) { if (value is null) _values.Remove(providerId); else _values[providerId] = value; }
        public void Clear() => _values.Clear();
    }
}

public class EvalGradingTests
{
    private static OptimizeResult Result(string draft, string prompt, Verdict verdict = Verdict.Rewritten) => new()
    {
        Original = draft, Prompt = prompt, TaskType = TaskType.General, Verdict = verdict, Model = "m",
    };

    [Fact]
    public void Flags_placeholders_filler_and_expansion()
    {
        var c = new EvalCase { Id = "t", Category = "simple", Draft = "capital of france?", MaxLengthRatio = 3 };
        var failures = EvalRunner.Grade(c, Result(c.Draft,
            "You are a world-class geographer. What is the capital of [insert country]? Explain step by step with sources and context."));
        Assert.Contains(failures, f => f.Contains("placeholder"));
        Assert.Contains(failures, f => f.Contains("filler"));
        Assert.Contains(failures, f => f.Contains("over-expanded"));
    }

    [Fact]
    public void Clean_result_passes()
    {
        var c = new EvalCase { Id = "t", Category = "coding", Draft = "fix getUser in users.ts", MaxLengthRatio = 20 };
        Assert.Empty(EvalRunner.Grade(c, Result(c.Draft, "Fix the bug in getUser in users.ts. Explain the cause and show the changed code.")));
    }

    [Fact]
    public void Placeholder_already_in_draft_is_not_flagged()
    {
        var c = new EvalCase { Id = "t", Category = "writing", Draft = "fill this template: Dear [your name], thanks" };
        Assert.Empty(EvalRunner.Grade(c, Result(c.Draft, "Fill in this template: Dear [your name], thanks")));
    }
}

public class EvalCasesFileTests
{
    [Fact]
    public void Shipped_eval_cases_parse_and_have_unique_ids()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "eval", "cases.json"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var cases = EvalRunner.LoadCases(Path.Combine(dir!, "eval", "cases.json"));
        Assert.True(cases.Count >= 40);
        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());
        Assert.Contains(cases, c => c.Mode == OptimizeMode.Detailed);
        Assert.Contains(cases, c => c.Repeat == 3);
        Assert.Contains(cases, c => c.WrittenIn is not null);
    }

    [Fact]
    public void Shipped_v1_0_0_system_prompt_is_a_full_prompt()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "eval", "prompts", "v1.0.0.txt"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!, "eval", "prompts", "v1.0.0.txt"));
        Assert.StartsWith("You are PromptForge", text);
        Assert.Contains("Respond with only one JSON object", text);
        Assert.NotEqual(SystemPrompt.Text, text.TrimEnd('\n'));
    }
}

public class EvalGradingNumberTests
{
    [Fact]
    public void Flags_numbers_and_missing_questions()
    {
        var c = new EvalCase { Id = "t", Category = "vague", Draft = "explain databases", ExpectQuestions = true };
        var result = new OptimizeResult { Original = c.Draft, Prompt = "Explain databases in 3 paragraphs.", TaskType = TaskType.Study, Verdict = Verdict.Rewritten, Model = "m" };
        var failures = EvalRunner.Grade(c, result);
        Assert.Contains(failures, f => f.Contains("no questions"));
        Assert.Contains(failures, f => f.Contains("added numbers") && f.Contains('3'));
    }

    [Fact]
    public void Numbers_from_the_draft_are_fine()
    {
        var c = new EvalCase { Id = "t", Category = "simple", Draft = "what does http 418 mean" };
        var result = new OptimizeResult { Original = c.Draft, Prompt = "What does HTTP status 418 mean?", TaskType = TaskType.General, Verdict = Verdict.LightEdit, Model = "m" };
        Assert.Empty(EvalRunner.Grade(c, result));
    }

    [Fact]
    public void Report_counts_dropped_requirements_from_the_judge()
    {
        var c = new EvalCase { Id = "t", Category = "stated-rule", Draft = "decide which issues to build, only ones that work in every situation" };
        var result = new OptimizeResult { Original = c.Draft, Prompt = "Decide which issues to build.", TaskType = TaskType.Planning, Verdict = Verdict.Rewritten, Model = "m" };
        var judge = new JudgeVerdict(false, false, "", true, true, "dropped the rule", DroppedRequirements: true, DroppedExamples: "only ones that work in every situation");
        var report = EvalRunner.BuildReport([new EvalCaseResult { Case = c, Result = result, Judge = judge }], "m");
        Assert.Contains("| Judge: dropped a stated requirement | 1/1 |", report);
        Assert.Contains("dropped=True only ones that work in every situation", report);
    }

    [Fact]
    public void Report_counts_changed_intent_from_the_judge()
    {
        var c = new EvalCase { Id = "t", Category = "intent", Draft = "also just merging to main deploy it to my laptop?" };
        var result = new OptimizeResult { Original = c.Draft, Prompt = "Explain how to merge to main and deploy to my laptop.", TaskType = TaskType.Planning, Verdict = Verdict.Rewritten, Model = "m" };
        var judge = new JudgeVerdict(false, false, "", true, true, "question became instruction", ChangedIntent: true);
        var report = EvalRunner.BuildReport([new EvalCaseResult { Case = c, Result = result, Judge = judge }], "m");
        Assert.Contains("| Judge: changed the kind of request | 1/1 |", report);
    }

    [Fact]
    public void Assistant_voice_is_left_to_the_judge_not_a_phrase_list()
    {
        var c = new EvalCase { Id = "t", Category = "voice", Draft = "i have turned on the memory" };
        var result = new OptimizeResult
        {
            Original = c.Draft,
            Prompt = "I have turned on the memory. Please let me know what you would like me to do with this information.",
            TaskType = TaskType.General, Verdict = Verdict.Rewritten, Model = "m",
        };
        Assert.Empty(EvalRunner.Grade(c, result));
        var judge = new JudgeVerdict(false, false, "", false, true, "reply voice", WrittenAsReply: true, AddedRequests: true, AddedExamples: "asks the user what to do");
        var report = EvalRunner.BuildReport([new EvalCaseResult { Case = c, Result = result, Judge = judge }], "m");
        Assert.Contains("| Judge: written as the AI's reply | 1/1 |", report);
        Assert.Contains("| Judge: added requests | 1/1 |", report);
        Assert.Contains("added=True asks the user what to do", report);
    }

    [Fact]
    public void Report_shows_pass_rate_per_case_lowest_first()
    {
        var flaky = new EvalCase { Id = "flaky", Category = "intent", Draft = "d" };
        var solid = new EvalCase { Id = "solid", Category = "simple", Draft = "d" };
        var ok = new OptimizeResult { Original = "d", Prompt = "d", TaskType = TaskType.General, Verdict = Verdict.LightEdit, Model = "m" };
        EvalCaseResult R(EvalCase c, int run, params string[] failures) => new() { Case = c, Run = run, Result = ok, Failures = [.. failures] };
        var results = new[]
        {
            R(solid, 1), R(solid, 2), R(solid, 3),
            R(flaky, 1, "judge: invented details (React)"), R(flaky, 2), R(flaky, 3, "judge: invented details (Vue)"),
        };
        var report = EvalRunner.BuildReport(results, new EvalRunInfo("m", "judge-model (gemini)", JudgeIndependent: true, InstructionsLabel: "v1.0.0.txt"));

        Assert.Contains("| Cases / runs | 2 / 6 |", report);
        Assert.Contains("| Cases with mixed results | 1 |", report);
        Assert.Contains("| flaky | intent | 1/3 | judge: invented details |", report);
        Assert.True(report.IndexOf("| flaky |", StringComparison.Ordinal) < report.IndexOf("| solid |", StringComparison.Ordinal));
        Assert.Contains("Judge: judge-model (gemini) (independent provider)", report);
        Assert.Contains("System prompt: v1.0.0.txt", report);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 3, 3)]
    [InlineData(5, 3, 5)]
    [InlineData(0, 0, 1)]
    public void Each_case_runs_at_least_its_own_repeat_count(int caseRepeat, int runRepeat, int expected) =>
        Assert.Equal(expected, EvalRunner.RunsFor(new EvalCase { Id = "t", Category = "c", Draft = "d", Repeat = caseRepeat }, runRepeat));

    [Fact]
    public async Task Runner_repeats_cases_and_uses_the_given_system_prompt()
    {
        var client = new PromptOptimizerTests.FakeClient(_ =>
            System.Text.Json.JsonSerializer.Serialize(new { task_type = "general", verdict = "light_edit", prompt = "What is DNS?", changes = Array.Empty<string>(), questions = Array.Empty<string>() }));
        var endpoint = new LlmEndpoint(ProviderPresets.Groq, "https://x", "m", "k");
        var runner = new EvalRunner(new PromptOptimizer(client, (_, _) => Task.CompletedTask), client);
        var results = await runner.RunAsync([new EvalCase { Id = "dns", Category = "simple", Draft = "what is dns", WrittenIn = "a web page" }], [endpoint], null,
            TimeSpan.Zero, null, CancellationToken.None, repeat: 3, options: new OptimizerOptions { Instructions = "old prompt" });

        Assert.Equal([1, 2, 3], results.Select(r => r.Run));
        Assert.All(client.Calls, c => Assert.Equal("old prompt", c.Messages[0].Content));
        Assert.All(client.Calls, c => Assert.Contains("written_in: a web page", c.Messages[1].Content));
    }

    [Fact]
    public void Comparison_separates_real_changes_from_noise()
    {
        static IEnumerable<CaseRun> Runs(string id, int passed, int runs) => Enumerable.Range(0, runs).Select(i => new CaseRun(id, i < passed));
        var before = Runs("fixed", 0, 3).Concat(Runs("broke", 3, 3)).Concat(Runs("wobble", 1, 3)).Concat(Runs("steady", 3, 3)).Concat(Runs("gone", 1, 1)).ToList();
        var after = Runs("fixed", 3, 3).Concat(Runs("broke", 1, 3)).Concat(Runs("wobble", 2, 3)).Concat(Runs("steady", 3, 3)).Concat(Runs("new", 1, 1)).ToList();
        var report = EvalComparison.Build(before, after, "v1.0.0", "main");

        Assert.Contains("| fixed | 0/3 | 3/3 | better |", report);
        Assert.Contains("| broke | 3/3 | 1/3 | worse |", report);
        Assert.Contains("| wobble | 1/3 | 2/3 | noise |", report);
        Assert.DoesNotContain("| steady |", report);
        Assert.Contains("Better: 1. Worse: 1. Noise: 1.", report);
        Assert.Contains("Only in before: gone", report);
        Assert.Contains("Only in after: new", report);
        Assert.True(report.IndexOf("| broke |", StringComparison.Ordinal) < report.IndexOf("| fixed |", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_run_flip_is_noise()
    {
        var report = EvalComparison.Build([new CaseRun("once", true)], [new CaseRun("once", false)], "a", "b");
        Assert.Contains("| once | 1/1 | 0/1 | noise |", report);
        Assert.Contains("counted as noise", report);
    }

    [Fact]
    public void Duplicate_case_ids_are_rejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """[{"id":"a","category":"c","draft":"x"},{"id":"a","category":"c","draft":"y"}]""");
            Assert.Throws<InvalidDataException>(() => EvalRunner.LoadCases(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Comparison_reads_results_json()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """[{"Id":"a","Category":"c","Run":1,"Passed":true,"Error":null},{"Id":"a","Category":"c","Run":2,"Passed":false,"Error":"x"}]""");
            Assert.Equal([new CaseRun("a", true), new CaseRun("a", false, Errored: true)], EvalComparison.LoadRuns(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Repair_message_asks_for_details_back_without_meta_instructions()
    {
        var message = UserMessageBuilder.BuildRepairMessage(["merge and deploy"]);
        Assert.Contains("- merge and deploy", message);
        Assert.Contains("Don't add an instruction telling the AI to use or include them", message);
    }
}
