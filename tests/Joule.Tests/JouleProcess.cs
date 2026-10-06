using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Joule;
using Xunit;

/// <summary>
/// Tests that start child APIs run on their own after the parallel tests, so the many ephemeral ports they bind cannot be
/// handed to a test that frees a port and expects nothing to be listening on it (PredbatMcpTransportTests).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChildProcessCollection { public const string Name = "Child API processes"; }

/// <summary>
/// Runs the real API as a child process on a random loopback port, with a private data directory and an optional fake
/// wwwroot, so middleware (auth, compression, caching, fallbacks) is tested exactly as it ships.
/// </summary>
sealed class JouleProcess : IAsyncDisposable
{
    readonly Process process;
    readonly TaskCompletionSource<string?> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<string> log = [];
    public string Directory { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "joule-test-" + Guid.NewGuid());
    public string Log { get { lock (log) return string.Join('\n', log); } }
    public int ExitCode => process.ExitCode;
    public Uri? BaseAddress { get; private set; }
    /// <summary>Cookies are not stored automatically, so tests see and control Set-Cookie exactly.</summary>
    public HttpClient Http { get; } = new(new HttpClientHandler { UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.None }) { Timeout = TimeSpan.FromSeconds(15) };

    public static string DotnetHost => Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet" + (OperatingSystem.IsWindows() ? ".exe" : "")));

    public JouleProcess(Dictionary<string, string?> environment, bool withWebRoot = false, params string[] args)
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (withWebRoot)
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "wwwroot", "assets"));
            File.WriteAllText(Path.Combine(Directory, "wwwroot", "index.html"), "<!doctype html><html><head><title>Joule</title><script type=\"module\" src=\"/assets/index-abc123.js\"></script></head><body><div id=\"root\"></div></body></html>");
            File.WriteAllText(Path.Combine(Directory, "wwwroot", "assets", "index-abc123.js"), string.Concat(Enumerable.Repeat("console.log('joule asset');\n", 400)));
            File.WriteAllText(Path.Combine(Directory, "wwwroot", "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        }
        var start = new ProcessStartInfo(DotnetHost) { WorkingDirectory = Directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(StateService).Assembly.Location);
        foreach (var a in args) start.ArgumentList.Add(a);
        foreach (var name in start.Environment.Keys.Where(x => new[] { "App__", "Predbat__", "HomeAssistant__", "ConfigFiles__", "Ai__", "ASPNETCORE_", "DOTNET_", "URLS" }.Any(prefix => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray())
            start.Environment.Remove(name);
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["App__DataDirectory"] = Directory;
        start.Environment["App__Demo"] = "true";
        foreach (var (key, value) in environment) { if (value is null) start.Environment.Remove(key); else start.Environment[key] = value; }
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Record(e.Data);
        process.ErrorDataReceived += (_, e) => Record(e.Data);
        process.Exited += (_, _) => ready.TrySetResult(null);
    }

    void Record(string? line)
    {
        if (line == null) return;
        lock (log) log.Add(line);
        var match = Regex.Match(line, @"Now listening on: http://(?:127\.0\.0\.1|0\.0\.0\.0|\[::\]):(\d+)");
        if (match.Success) ready.TrySetResult($"http://127.0.0.1:{match.Groups[1].Value}");
    }

    /// <summary>Starts the process; true once it listens, false if it exited first.</summary>
    public async Task<bool> Start()
    {
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var url = await ready.Task.WaitAsync(TimeSpan.FromSeconds(40));
        if (url == null) { await process.WaitForExitAsync(); return false; }
        BaseAddress = new Uri(url);
        Http.BaseAddress = BaseAddress;
        return true;
    }

    /// <summary>Runs the process to completion (for command-line switches such as --health).</summary>
    public async Task<int> RunToExit(TimeSpan timeout)
    {
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cts.Token);
        process.WaitForExit();
        return process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
        process.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
