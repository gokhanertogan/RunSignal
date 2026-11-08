using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace RunSignal.Cli;

/// <summary><c>runsignal discover kubernetes</c>: writes a manifest from the CronJobs in a cluster.</summary>
internal static class DiscoverCommand
{
    public static Command Create(TextWriter output, TextWriter error, Func<Func<ParseResult, CancellationToken, Task<int>>, Func<ParseResult, CancellationToken, Task<int>>> guarded)
    {
        var service = new Option<string>("--service")
        {
            Description = "The service.name the jobs report as (runsignal exec --service, or OTEL_SERVICE_NAME).",
            Required = true,
        };
        var input = new Option<string?>("--input", "-i")
        {
            Description = "Read CronJobs from this file (kubectl get cronjobs -o json), or - for standard input, instead of running kubectl.",
        };
        var ns = new Option<string?>("--namespace", "-n") { Description = "The namespace to read CronJobs from. Defaults to kubectl's current namespace." };
        var allNamespaces = new Option<bool>("--all-namespaces", "-A") { Description = "Read CronJobs from every namespace." };
        var context = new Option<string?>("--context") { Description = "The kubeconfig context to use." };
        var outputFile = new Option<FileInfo?>("--output", "-o") { Description = "Write the manifest to this file instead of standard output." };

        var kubernetes = new Command("kubernetes", "Write a manifest from Kubernetes CronJobs whose containers run their command through runsignal exec. Schedules, time zones and activeDeadlineSeconds come from the CronJobs; runsignal.io/* annotations set the rest.")
        {
            service, input, ns, allNamespaces, context, outputFile,
        };
        kubernetes.Aliases.Add("k8s");
        kubernetes.SetAction(guarded(async (result, ct) =>
        {
            var json = result.GetValue(input) switch
            {
                "-" => await Console.In.ReadToEndAsync(ct).ConfigureAwait(false),
                { } path => await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
                null => await KubectlAsync(result.GetValue(ns), result.GetValue(allNamespaces), result.GetValue(context), ct).ConfigureAwait(false),
            };

            using var document = JsonDocument.Parse(json);
            var manifest = KubernetesCronJobs.ToManifest(document.RootElement, result.GetValue(service)!, out var warnings);
            foreach (var warning in warnings)
                await error.WriteLineAsync("warning: " + warning).ConfigureAwait(false);

            var text = manifest.ToJson() + Environment.NewLine;
            if (result.GetValue(outputFile) is { } file)
            {
                await File.WriteAllTextAsync(file.FullName, text, ct).ConfigureAwait(false);
                await output.WriteLineAsync($"Wrote {manifest.Workloads.Count} workloads to {file.FullName}.").ConfigureAwait(false);
            }
            else
            {
                await output.WriteAsync(text).ConfigureAwait(false);
            }
            return 0;
        }));

        return new Command("discover", "Write a workload manifest from where jobs are scheduled, for jobs that report through runsignal exec.") { kubernetes };
    }

    private static async Task<string> KubectlAsync(string? ns, bool allNamespaces, string? context, CancellationToken ct)
    {
        var start = new ProcessStartInfo("kubectl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in (string[])["get", "cronjobs", "-o", "json"])
            start.ArgumentList.Add(argument);
        if (allNamespaces)
            start.ArgumentList.Add("--all-namespaces");
        else if (ns is not null)
            start.ArgumentList.Add("--namespace=" + ns);
        if (context is not null)
            start.ArgumentList.Add("--context=" + context);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("kubectl did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new IOException($"Could not run kubectl ({ex.Message}). Install it, or pass --input with the output of kubectl get cronjobs -o json.", ex);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException("kubectl get cronjobs failed: " + (await stderr.ConfigureAwait(false)).Trim());
            return await stdout.ConfigureAwait(false);
        }
    }
}
