using System.Diagnostics;

namespace ClaimFlow.Bench;

/// <summary>Starts the two microservices as child processes for the duration of a benchmark.</summary>
public sealed class ServiceHostRunner : IAsyncDisposable
{
    private readonly BenchOptions _options;
    private readonly string _signingKey;
    private readonly List<Process> _processes = new();
    private readonly List<StreamWriter> _logWriters = new();
    private readonly object _logGate = new();
    private readonly string _logDirectory;

    public ServiceHostRunner(BenchOptions options, string signingKey)
    {
        _options = options;
        _signingKey = signingKey;
        _logDirectory = Path.Combine(
            Path.GetTempPath(),
            "claimflow-bench",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_logDirectory);
        Console.WriteLine($"Service logs: {_logDirectory}");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.SkipStart)
        {
            Console.WriteLine("Using already-running services (--skip-start).");
            await WaitForHealthAsync(cancellationToken);
            return;
        }

        StartService("intake", "src/ClaimFlow.Intake", _options.IntakeUrl,
            new Dictionary<string, string>
            {
                ["Outbox__PollIntervalMs"] = "50",
            });
        StartService("adjudication", "src/ClaimFlow.Adjudication", _options.AdjudicationUrl,
            new Dictionary<string, string>
            {
                ["Intake__BaseUrl"] = _options.IntakeUrl,
            });

        await WaitForHealthAsync(cancellationToken);
    }

    private void StartService(string name, string project, string url, Dictionary<string, string> extra)
    {
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _options.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(project);
        info.ArgumentList.Add("--no-build");
        info.ArgumentList.Add("--no-launch-profile");

        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        info.Environment["ASPNETCORE_URLS"] = url;
        info.Environment["Database__Provider"] = "SqlServer";
        info.Environment["ConnectionStrings__ClaimFlow"] = _options.ConnectionString;
        info.Environment["Messaging__Provider"] = "RabbitMq";
        info.Environment["RabbitMq__HostName"] = _options.RabbitHost;
        info.Environment["RabbitMq__Port"] = _options.RabbitPort.ToString();
        info.Environment["RabbitMq__UserName"] = _options.RabbitUser;
        info.Environment["RabbitMq__Password"] = _options.RabbitPassword;
        info.Environment["Jwt__SigningKey"] = _signingKey;
        info.Environment["Jwt__Issuer"] = "claimflow-bench";
        info.Environment["Jwt__Audience"] = "claimflow-bench-api";

        foreach (var (key, value) in extra)
        {
            info.Environment[key] = value;
        }

        var logPath = Path.Combine(_logDirectory, $"{name}.log");
        var writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
        _logWriters.Add(writer);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => Log(writer, args.Data);
        process.ErrorDataReceived += (_, args) => Log(writer, args.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add(process);
        Console.WriteLine($"Started {name} on {url} (pid {process.Id}).");
    }

    private void Log(StreamWriter writer, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logGate)
        {
            writer.WriteLine(line);
        }
    }

    public async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var targets = new[] { _options.IntakeUrl, _options.AdjudicationUrl };
        var deadline = DateTime.UtcNow.AddMinutes(90);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_processes.Any(p => p.HasExited))
            {
                var exited = _processes.First(p => p.HasExited);
                throw new InvalidOperationException(
                    $"Service process {exited.Id} exited with code {exited.ExitCode}.\n{ReadLogTail()}");
            }

            var healthy = 0;
            foreach (var target in targets)
            {
                try
                {
                    using var response = await client.GetAsync($"{target}/health/ready", cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        healthy++;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not up yet.
                }
                catch (TaskCanceledException)
                {
                    // Timeout while starting.
                }
            }

            if (healthy == targets.Length)
            {
                Console.WriteLine("Both services report ready.");
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"Services did not become healthy.\n{ReadLogTail()}");
    }

    public string ReadLogTail(int lines = 40)
    {
        var output = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(_logDirectory, "*.log"))
        {
            output.AppendLine($"--- {Path.GetFileName(file)} ---");
            var tail = File.ReadLines(file).TakeLast(lines);
            foreach (var line in tail)
            {
                output.AppendLine(line);
            }
        }

        return output.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
                }
            }
            catch (Exception)
            {
                // Best-effort shutdown.
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var writer in _logWriters)
        {
            await writer.DisposeAsync();
        }
    }
}
