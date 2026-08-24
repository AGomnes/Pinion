using System.CommandLine;
using Pinion.Adapters.CSharp;
using Pinion.Adapters.CSharp.Generate;
using Pinion.Engine.Analysis;
using Pinion.Engine.Model;
using Pinion.Generate;
using Pinion.Generate.Licensing;

namespace Pinion.Cli;

/// <summary>
/// `pinion generate` — writes characterization tests that LOCK the
/// current behavior of chosen targets (golden masters), with a compile→run→repair loop.
/// </summary>
internal static class GenerateCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path")
        {
            Description = "Path to the .sln/.csproj/dir containing the code to characterize.",
        };
        var testProjectOption = new Option<FileInfo?>("--test-project", "-p")
        {
            Description = "Test project (.csproj) to host the generated tests. Must reference the code under test, xunit, and VerifyXunit.",
        };
        var targetOption = new Option<string?>("--target", "-t")
        {
            Description = "Only characterize methods whose name/id contains this substring.",
        };
        var topOption = new Option<int>("--top")
        {
            Description = "When --target is omitted, characterize the top N high-risk untested methods.",
            DefaultValueFactory = _ => 1,
        };
        var providerOption = new Option<string>("--provider")
        {
            Description = "Generator: 'deterministic' (default, offline, no AI). AI providers are opt-in: 'anthropic', 'openai', 'azure-openai' (each needs its own API key env var), plus 'heuristic' for offline pipeline testing.",
            DefaultValueFactory = _ => "deterministic",
        };
        var modelOption = new Option<string>("--model")
        {
            Description = "Model id for generation. Defaults per provider; for azure-openai this is your DEPLOYMENT name and is required.",
            DefaultValueFactory = _ => "",
        };
        var baseUrlOption = new Option<string>("--base-url")
        {
            Description = "Override the API base URL (a local or self-hosted endpoint, a gateway, or your Azure OpenAI resource). Defaults to the selected provider's own host.",
            DefaultValueFactory = _ => "",
        };
        var maxRepairsOption = new Option<int>("--max-repairs")
        {
            Description = "Max compile/run repair attempts per target.",
            DefaultValueFactory = _ => GenerationOptions.Default.MaxRepairAttempts,
        };
        var maxSpendOption = new Option<decimal>("--max-spend")
        {
            Description = "Hard ceiling on estimated API spend (USD) for the run; stops before exceeding it.",
            DefaultValueFactory = _ => 5.00m,
        };
        var maxTargetsOption = new Option<int>("--max-targets")
        {
            Description = "Safety cap on how many methods one run may characterize.",
            DefaultValueFactory = _ => 25,
        };
        var allowSideEffectsOption = new Option<bool>("--allow-side-effects")
        {
            Description = "Include methods tagged io/money. WARNING: running these executes real side effects (files, DB, network, payments).",
        };
        var excludeOption = new Option<string[]>("--exclude")
        {
            Description = "Exclude methods whose file/name/id matches (substring or glob; repeatable). Also reads .pinionignore.",
            AllowMultipleArgumentsPerToken = true,
        };
        var noSendOption = new Option<string[]>("--no-send")
        {
            Description = "Mark files/namespaces whose source must NEVER be sent to the AI (substring/glob; repeatable). Also reads .pinionnosend. Such methods are still locally characterizable with --provider deterministic.",
            AllowMultipleArgumentsPerToken = true,
        };
        var timeoutOption = new Option<int>("--timeout")
        {
            Description = "Per-method test-run timeout in seconds (kills a hung/infinite-loop target).",
            DefaultValueFactory = _ => 180,
        };
        var licenseOption = new Option<string?>("--license")
        {
            Description = "License key (else PINION_LICENSE env / pinion.license file). Dormant: nothing is gated.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show exactly what would be sent to the model, in that provider's own wire format, and make no API call.",
        };
        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Print pipeline + diagnostics to stderr.",
        };

        var cmd = new Command("generate", "Generate characterization tests that lock current behavior. Deterministic and offline by default; AI providers are opt-in.")
        {
            pathArg, testProjectOption, targetOption, topOption, providerOption,
            modelOption, baseUrlOption, maxRepairsOption, maxSpendOption, maxTargetsOption,
            allowSideEffectsOption, excludeOption, noSendOption, timeoutOption,
            licenseOption, dryRunOption, verboseOption,
        };

        cmd.SetAction(async (parse, ct) => await RunAsync(
            parse.GetValue(pathArg)!,
            parse.GetValue(testProjectOption),
            parse.GetValue(targetOption),
            parse.GetValue(topOption),
            parse.GetValue(providerOption)!,
            parse.GetValue(modelOption)!,
            parse.GetValue(baseUrlOption)!,
            parse.GetValue(maxRepairsOption),
            parse.GetValue(maxSpendOption),
            parse.GetValue(maxTargetsOption),
            parse.GetValue(allowSideEffectsOption),
            parse.GetValue(excludeOption) ?? Array.Empty<string>(),
            parse.GetValue(noSendOption) ?? Array.Empty<string>(),
            parse.GetValue(timeoutOption),
            parse.GetValue(licenseOption),
            parse.GetValue(dryRunOption),
            parse.GetValue(verboseOption),
            ct));

        return cmd;
    }

    private static async Task<int> RunAsync(
        string path, FileInfo? testProject, string? target, int top,
        string provider, string model, string baseUrl, int maxRepairs,
        decimal maxSpend, int maxTargets, bool allowSideEffects, string[] exclude, string[] noSend, int timeoutSeconds,
        string? license, bool dryRun, bool verbose, CancellationToken ct)
    {
        Action<string> log = msg => Console.Error.WriteLine(msg);
        Action<string>? vlog = verbose ? log : null;

        if (!dryRun)
        {
            var (status, source) = LicenseGate.Resolve(license);
            if (status.Valid)
            {
                Console.Error.WriteLine($"Licensed to {status.Claims!.Subject} ({status.Claims.Edition}, expires {status.Claims.Expires:yyyy-MM-dd}) [{source}].");
                LicenseCommand.WarnIfNearExpiry(status);
            }
        }

        try
        {
            bool isVb = AnalyzeCommand.IsVisualBasic(path);
            if (isVb && !provider.Equals("deterministic", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("error: VB.NET targets support --provider deterministic only (the default). The AI providers are C#-only for now.");
                return 1;
            }

            Pinion.Engine.Abstractions.ILanguageAdapter adapter = isVb
                ? new Pinion.Adapters.VisualBasic.VisualBasicAdapter(vlog)
                : new CSharpAdapter(vlog);

            Console.Error.WriteLine($"Analyzing {path} …");
            var units = await adapter.AnalyzeAsync(path, ct);
            var matched = SelectTargets(path, units, target, top);

            if (matched.Count == 0)
            {
                Console.Error.WriteLine("No matching targets to characterize.");
                return 1;
            }

            var exclusions = LoadPatterns(path, exclude, ".pinionignore");
            var notExcluded = matched.Where(u => !TargetGuards.IsExcluded(u, exclusions)).ToList();
            if (matched.Count != notExcluded.Count)
                Console.Error.WriteLine($"Excluded {matched.Count - notExcluded.Count} method(s) by exclude rules.");

            List<CodeUnit> runnable;
            if (allowSideEffects)
            {
                runnable = notExcluded;
            }
            else
            {
                runnable = notExcluded.Where(u => !TargetGuards.IsSideEffecting(u)).ToList();
                int skipped = notExcluded.Count - runnable.Count;
                if (skipped > 0)
                    Console.Error.WriteLine($"Skipped {skipped} method(s) tagged io that may touch the filesystem/DB/network when run — pass --allow-side-effects to include them.");
            }

            int sensitive = runnable.Count(TargetGuards.IsSensitive);
            if (sensitive > 0)
                Console.Error.WriteLine($"note: {sensitive} money/auth-sensitive method(s) will be characterized — snapshots are secret-scrubbed, but review them before committing.");

            if (runnable.Count == 0)
            {
                Console.Error.WriteLine("No runnable targets after exclusion/side-effect filters.");
                return 1;
            }

            var targets = runnable.Take(Math.Max(1, maxTargets)).ToList();
            if (runnable.Count > targets.Count)
                Console.Error.WriteLine($"Selected {runnable.Count} methods; capping to {targets.Count} (--max-targets). Raise --max-targets to do more.");

            using var genAdapter = new CSharpTestGenerator
            {
                RunTimeoutSeconds = timeoutSeconds,
                ResolveReferences = Microsoft.Build.Locator.MSBuildLocator.IsRegistered,
                Log = vlog,
            };

            if (provider.Equals("deterministic", StringComparison.OrdinalIgnoreCase))
                return await RunDeterministicAsync(genAdapter, targets, path, testProject, dryRun, ct);

            // Every provider below is OPT-IN: reached only because the user passed --provider
            // explicitly. The default is 'deterministic', which returned above and never touches the
            // network. An API key sitting in the environment selects nothing on its own.
            var (llm, resolvedModel, providerError) = CreateLlmClient(provider, model, baseUrl, dryRun);
            if (llm is null) return providerError;
            model = resolvedModel;

            if (!dryRun)
            {
                if (testProject is null)
                {
                    Console.Error.WriteLine("error: --test-project is required to emit and run tests (omit only with --dry-run).");
                    return 1;
                }
                genAdapter.ConfigureGeneration(testProject.FullName);
            }

            var options = GenerationOptions.Default with
            {
                Model = model,
                MaxRepairAttempts = maxRepairs,
                DryRun = dryRun,
            };
            bool billable = !dryRun && llm is AnthropicClient;
            var meter = new UsageMeter();

            var neverSend = LoadPatterns(path, noSend, ".pinionnosend");
            int withheld = neverSend.Count == 0 ? 0 : targets.Count(u => TargetGuards.IsNeverSend(u, neverSend));
            if (withheld > 0)
                Console.Error.WriteLine($"Never-send: {withheld} of {targets.Count} target(s) match a no-send rule — their source will not be sent to {llm.Name}; the AI path is refused for them (characterize offline with --provider deterministic).");

            var generator = new TestGenerator(genAdapter, llm, options, log,
                meter, maxSpendUsd: billable ? maxSpend : null, neverSend: neverSend);

            if (billable)
                Console.Error.WriteLine($"Characterizing up to {targets.Count} method(s) via {llm.Name} ({model}), " +
                    $"≤{maxRepairs + 1} call(s) each; spend ceiling ${maxSpend:0.00}.");
            else
                Console.Error.WriteLine($"Characterizing {targets.Count} target(s) via {llm.Name} ({model}){(dryRun ? " [dry-run]" : "")} …");

            int ok = 0;
            foreach (var unit in targets)
            {
                if (generator.SpendCeilingReached)
                {
                    Console.Error.WriteLine($"Spend ceiling ${maxSpend:0.00} reached — stopping ({meter.Summary()}).");
                    break;
                }

                var result = await generator.GenerateAsync(unit, ct);
                if (result.Success)
                {
                    ok++;
                    Console.Out.WriteLine($"✓ {unit.DisplayName} → {result.TestFilePath}");
                    Console.Out.WriteLine($"  golden master: {result.SnapshotPath}");
                }
                else if (!dryRun)
                {
                    Console.Out.WriteLine($"✗ {unit.DisplayName} (gave up after {result.Attempts} attempt(s))");
                    foreach (var d in result.Diagnostics.Take(5)) Console.Out.WriteLine($"    {d}");
                }
            }

            if (dryRun) return 0;
            if (billable) Console.Error.WriteLine($"Usage: {meter.Summary()}");
            Console.Error.WriteLine($"Done: {ok}/{targets.Count} characterized.");
            return ok == targets.Count ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (LlmApiException ex) when (ex.ShouldAbortRun)
        {
            Console.Error.WriteLine($"error: stopping the run to avoid further usage — {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (verbose) Console.Error.WriteLine(ex);
            return 1;
        }
    }


    /// <summary>
    /// Build the opt-in AI client for <paramref name="provider"/>. Returns (null, exitCode) with a
    /// message already printed when the provider is unusable.
    ///
    /// On --dry-run the real provider's client is still constructed, with an empty key, so the preview
    /// shows that provider's ACTUAL wire format. No request is sent: TestGenerator returns before the
    /// send. Substituting a different client here would make --dry-run print a payload the tool would
    /// never send, defeating the point of the flag.
    /// </summary>
    private static (ILlmClient? Client, string Model, int ExitCode) CreateLlmClient(
        string provider, string model, string baseUrl, bool dryRun)
    {
        string p = provider.ToLowerInvariant();

        if (p == "heuristic") return (new HeuristicLlmClient(), model, 0);
        if (p == "heuristic-faulty") return (new FaultInjectingLlmClient(new HeuristicLlmClient(), failFirst: 1), model, 0);

        (string keyVar, string defaultBase) = p switch
        {
            "anthropic" => ("ANTHROPIC_API_KEY", "https://api.anthropic.com"),
            "openai" => ("OPENAI_API_KEY", OpenAiClient.DefaultBaseUrl),
            "azure-openai" => ("AZURE_OPENAI_API_KEY", ""),
            _ => ("", ""),
        };

        if (keyVar.Length == 0)
        {
            Console.Error.WriteLine($"error: unknown --provider '{provider}'. Valid: deterministic (default), " +
                "anthropic, openai, azure-openai, heuristic, heuristic-faulty.");
            return (null, model, 1);
        }

        // --model defaults per provider, so `--provider openai` doesn't inherit an Anthropic id and 404.
        if (string.IsNullOrWhiteSpace(model))
        {
            model = ModelCatalog.DefaultFor(p) ?? "";
            if (model.Length == 0)
            {
                Console.Error.WriteLine($"error: --provider {p} has no default model. Pass --model " +
                    "(for azure-openai, your deployment name).");
                return (null, model, 1);
            }
        }

        string resolvedBase = string.IsNullOrWhiteSpace(baseUrl) ? defaultBase : baseUrl;
        if (p == "azure-openai" && resolvedBase.Length == 0)
        {
            Console.Error.WriteLine("error: --provider azure-openai needs --base-url pointing at your resource " +
                "(e.g. https://my-resource.openai.azure.com), and --model set to your deployment name.");
            return (null, model, 1);
        }

        // A key is only required to actually send. --dry-run builds the same client with an empty key
        // so a prospect can audit the exact payload without holding an account.
        string apiKey = Environment.GetEnvironmentVariable(keyVar) ?? "";
        if (!dryRun && apiKey.Length == 0)
        {
            Console.Error.WriteLine($"error: {keyVar} is not set. Set it, use the default --provider deterministic, or --dry-run.");
            return (null, model, 1);
        }

        if (ModelCatalog.ShouldWarn(p, model))
            Console.Error.WriteLine($"warning: '{model}' is not a model id Pinion recognizes for {p}. " +
                $"Known: {string.Join(", ", ModelCatalog.KnownFor(p))}. Proceeding — pass a correct id if this was a typo.");

        if (!dryRun) WarnIfBaseUrlExposesKey(resolvedBase, defaultBase, keyVar);

        return p switch
        {
            "anthropic" => (new AnthropicClient(apiKey, resolvedBase), model, 0),
            "openai" => (new OpenAiClient(apiKey, resolvedBase), model, 0),
            _ => (new OpenAiClient(apiKey, resolvedBase, OpenAiClient.AuthStyle.AzureApiKey, azureDeployment: model), model, 0),
        };
    }
    /// <summary>
    /// The API key is attached to every request regardless of --base-url. Make key egress visible: warn
    /// when the key would go anywhere other than the provider's own host (a proxy, a typo'd host, a local
    /// model), and again when it would travel in cleartext to a remote host. Advisory only —
    /// http://localhost for an air-gapped local model is a supported use, so this never blocks; it just
    /// ensures the user sees where their key is going.
    /// </summary>
    private static void WarnIfBaseUrlExposesKey(string baseUrl, string providerDefault, string keyVar)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine($"warning: --base-url '{baseUrl}' is not a valid absolute URL.");
            return;
        }

        // Azure has no single canonical host (every tenant differs), so treat any *.openai.azure.com
        // as expected rather than warning on every legitimate resource name.
        bool expected = Uri.TryCreate(providerDefault, UriKind.Absolute, out var def)
            && uri.Host.Equals(def.Host, StringComparison.OrdinalIgnoreCase);
        expected |= uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase);
        if (expected) return;

        Console.Error.WriteLine($"warning: --base-url points at {uri.Host} — your {keyVar} will be sent " +
            "there. Only use an endpoint you trust.");
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !uri.IsLoopback)
            Console.Error.WriteLine($"warning: --base-url uses {uri.Scheme} (not https) to a remote host — the API key would travel in cleartext.");
    }

    private static async Task<int> RunDeterministicAsync(
        CSharpTestGenerator genAdapter, IReadOnlyList<CodeUnit> targets, string sourceRoot,
        FileInfo? testProject, bool dryRun, CancellationToken ct)
    {
        if (dryRun)
        {
            foreach (var unit in targets)
            {
                Console.Out.WriteLine($"// ---- {unit.DisplayName} — deterministic, would be written; nothing sent anywhere ----");
                try { Console.Out.WriteLine(genAdapter.SynthesizeDeterministic(unit, sourceRoot, ct)); }
                catch (Exception ex) { Console.Error.WriteLine($"  (could not synthesize {unit.DisplayName}: {ex.Message})"); }
            }
            return 0;
        }

        if (testProject is null)
        {
            Console.Error.WriteLine("error: --test-project is required to emit and run tests (omit only with --dry-run).");
            return 1;
        }
        genAdapter.ConfigureGeneration(testProject.FullName);

        Console.Error.WriteLine($"Characterizing {targets.Count} method(s) — deterministic, offline (no AI, no API cost), one build for the batch.");

        var results = await genAdapter.GenerateDeterministicBatchAsync(targets, sourceRoot, ct);

        int ok = 0;
        foreach (var result in results)
        {
            if (result.Success)
            {
                ok++;
                Console.Out.WriteLine($"✓ {result.Unit.DisplayName} → {result.TestFilePath}");
                Console.Out.WriteLine($"  golden master: {result.SnapshotPath}");
            }
            else
            {
                Console.Out.WriteLine($"✗ {result.Unit.DisplayName}");
                foreach (var d in result.Diagnostics.Take(3)) Console.Out.WriteLine($"    {d}");
            }
        }

        Console.Error.WriteLine($"Done: {ok}/{targets.Count} characterized (deterministic, $0).");
        return ok == targets.Count ? 0 : 1;
    }

    /// <summary>Collect glob/substring patterns from repeatable CLI args plus a dotfile (e.g.
    /// <c>.pinionignore</c> for exclusions, <c>.pinionnosend</c> for never-send) next to the target
    /// and in the working directory. Blank lines and <c>#</c> comments are ignored.</summary>
    private static List<string> LoadPatterns(string path, string[] cli, string fileName)
    {
        var patterns = new List<string>();
        foreach (var e in cli)
            patterns.AddRange(e.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        string root = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))!
            : Directory.Exists(path) ? Path.GetFullPath(path)
            : Directory.GetCurrentDirectory();

        foreach (var file in new[] { Path.Combine(root, fileName), Path.Combine(Directory.GetCurrentDirectory(), fileName) })
        {
            if (!File.Exists(file)) continue;
            foreach (var line in File.ReadAllLines(file))
            {
                string t = line.Trim();
                if (t.Length > 0 && !t.StartsWith('#')) patterns.Add(t);
            }
        }

        return patterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<CodeUnit> SelectTargets(string path, IReadOnlyList<CodeUnit> units, string? target, int top)
    {
        if (!string.IsNullOrWhiteSpace(target))
        {
            return units
                .Where(u => u.DisplayName.Contains(target!, StringComparison.OrdinalIgnoreCase)
                         || u.Id.Contains(target!, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var report = ReportBuilder.Build(path, units);
        return report.Hotspots
            .Where(s => !s.Unit.HasTests && s.Unit.IsPublicEntryPoint)
            .Take(Math.Max(1, top))
            .Select(s => s.Unit)
            .ToList();
    }
}
