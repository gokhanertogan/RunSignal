using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace RunSignal.Cli;

/// <summary>
/// <c>runsignal exec</c>: runs a command as one run of a workload and exports it over OTLP, for work that has no
/// RunSignal SDK, such as shell scripts, crontab entries and Kubernetes CronJobs. Its workloads report per run.
/// </summary>
internal static class ExecCommand
{
    private const string TemporalityVariable = "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE";
    private const string IntervalVariable = "OTEL_METRIC_EXPORT_INTERVAL";

    // Exit code shells use for a command that cannot be found.
    private const int CommandNotFound = 127;

    // Signals that ask a process to stop. They are forwarded to the command, and the run counts as cancelled.
    private static readonly (PosixSignal Signal, int Number)[] s_stopSignals =
    [
        (PosixSignal.SIGTERM, 15),
        (PosixSignal.SIGINT, 2),
        (PosixSignal.SIGQUIT, 3),
        (PosixSignal.SIGHUP, 1),
    ];

    /// <param name="error">Where RunSignal's own messages go; the command's output is passed through untouched.</param>
    /// <param name="configureMetrics">Replaces the OTLP exporter; tests use it to read what a run recorded.</param>
    public static Command Create(TextWriter error, Action<MeterProviderBuilder>? configureMetrics)
    {
        var workloadName = new Option<string>("--workload", "-w")
        {
            Description = "The workload this command is a run of, such as nightly-invoice. It must match the manifest.",
            Required = true,
        };
        var service = new Option<string?>("--service")
        {
            Description = "The service.name to report as, matching the manifest's service. Defaults to OTEL_SERVICE_NAME.",
        };
        var temporality = new Option<string?>("--temporality")
        {
            Description = $"delta (required by New Relic) or cumulative (Prometheus). Defaults to {TemporalityVariable}, then cumulative.",
        };
        var command = new Argument<string[]>("command")
        {
            Description = "The command and its arguments, after --.",
            Arity = ArgumentArity.OneOrMore,
        };

        var exec = new Command("exec", "Run a command as one run of a workload and export it over OTLP: success on exit code 0, failure otherwise, cancelled when stopped by a signal. Configure the exporter with the standard OTEL_EXPORTER_OTLP_* variables. Returns the command's exit code.")
        {
            workloadName, service, temporality, command,
        };
        exec.SetAction(async (result, ct) =>
        {
            var serviceName = result.GetValue(service) ?? Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                await error.WriteLineAsync("error: pass --service or set OTEL_SERVICE_NAME to the service in the manifest.").ConfigureAwait(false);
                return 2;
            }

            MetricReaderTemporalityPreference preference;
            try
            {
                preference = ParseTemporality(result.GetValue(temporality) ?? Environment.GetEnvironmentVariable(TemporalityVariable));
            }
            catch (FormatException ex)
            {
                await error.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
                return 2;
            }

            using var registry = new WorkloadRegistry();
            Workload workload;
            try
            {
                workload = registry.Get(result.GetValue(workloadName)!);
            }
            catch (ArgumentException ex)
            {
                await error.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
                return 2;
            }

            var builder = Sdk.CreateMeterProviderBuilder()
                // A new instance per run, so Prometheus sees each run as its own series: a counter that starts at 1
                // in every process would otherwise look unchanged to increase(). The rules sum each run's series.
                .ConfigureResource(r => r.AddService(serviceName, serviceInstanceId: Guid.NewGuid().ToString("N")))
                .AddRunSignalInstrumentation();
            if (configureMetrics is not null)
            {
                configureMetrics(builder);
            }
            else
            {
                builder.AddOtlpExporter((_, reader) =>
                {
                    reader.TemporalityPreference = preference;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = ExportInterval();
                });
            }

            using var provider = builder.Build();
            var code = await RunAsync(workload, result.GetValue(command)!, error, ct).ConfigureAwait(false);

            // The process ends here, so this export is the only one short runs get.
            if (!provider.ForceFlush(10_000))
                await error.WriteLineAsync("runsignal: warning: the run could not be exported; check OTEL_EXPORTER_OTLP_ENDPOINT.").ConfigureAwait(false);
            return code;
        });
        return exec;
    }

    private static async Task<int> RunAsync(Workload workload, string[] command, TextWriter error, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0]) { UseShellExecute = false };
        foreach (var argument in command.Skip(1))
            start.ArgumentList.Add(argument);

        var run = workload.Start();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new Win32Exception("The process did not start.");
        }
        catch (Win32Exception ex)
        {
            run.Fail("command_not_found", ex.Message);
            await error.WriteLineAsync($"runsignal: {command[0]}: {ex.Message}").ConfigureAwait(false);
            return CommandNotFound;
        }

        using (process)
        {
            var stopped = 0;
            var registrations = new List<PosixSignalRegistration>();
            foreach (var (signal, number) in s_stopSignals)
            {
                try
                {
                    registrations.Add(PosixSignalRegistration.Create(signal, context =>
                    {
                        // Let the command decide how to stop, and keep running until it has, so the run is recorded.
                        context.Cancel = true;
                        Interlocked.Exchange(ref stopped, 1);
                        Forward(process, number);
                    }));
                }
                catch (PlatformNotSupportedException)
                {
                    // Windows has no SIGQUIT or SIGHUP; console signals reach the command directly there.
                }
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                foreach (var registration in registrations)
                    registration.Dispose();
            }

            var code = process.ExitCode;
            if (Volatile.Read(ref stopped) == 1)
                run.Cancel();
            else if (code == 0)
                run.Succeed();
            else
                run.Fail("exit_code_" + code.ToString(CultureInfo.InvariantCulture));
            return code;
        }
    }

    private static void Forward(Process process, int signal)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            if (!process.HasExited)
                _ = Kill(process.Id, signal);
        }
        catch (InvalidOperationException)
        {
            // The command already exited.
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    internal static MetricReaderTemporalityPreference ParseTemporality(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "cumulative" => MetricReaderTemporalityPreference.Cumulative,
        "delta" => MetricReaderTemporalityPreference.Delta,
        _ => throw new FormatException($"Temporality '{value}' is not delta or cumulative."),
    };

    // Long runs report active_run_age while they run, so the hung alert can fire before they end.
    private static int ExportInterval() =>
        int.TryParse(Environment.GetEnvironmentVariable(IntervalVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? ms
            : 15_000;
}
