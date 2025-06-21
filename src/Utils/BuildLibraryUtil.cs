using Microsoft.Extensions.Logging;
using Soenneker.Git.Runners.Linux.Utils.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Utils.HttpClientCache.Abstract;
using Soenneker.Utils.Process.Abstract;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Linux.Utils;

/// <inheritdoc cref="IBuildLibraryUtil"/>
public sealed class BuildLibraryUtil : IBuildLibraryUtil
{
    private const string ReproEnv = "SOURCE_DATE_EPOCH=1620000000 TZ=UTC LC_ALL=C";

    private const string InstallScript = "sudo apt-get update && " +
                                         "sudo apt-get install -y build-essential musl-tools pkg-config libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev tcl-dev tk-dev perl libperl-dev libreadline-dev gettext";

    private readonly ILogger<BuildLibraryUtil> _logger;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IHttpClientCache _httpClientCache;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IProcessUtil _processUtil;

    public BuildLibraryUtil(ILogger<BuildLibraryUtil> logger, IDirectoryUtil directoryUtil, IHttpClientCache httpClientCache,
        IFileDownloadUtil fileDownloadUtil, IProcessUtil processUtil)
    {
        _logger = logger;
        _directoryUtil = directoryUtil;
        _httpClientCache = httpClientCache;
        _fileDownloadUtil = fileDownloadUtil;
        _processUtil = processUtil;
    }

    public async ValueTask<string> Build(CancellationToken cancellationToken)
    {
        // 1) prepare temp dir
        string tempDir = _directoryUtil.CreateTempDirectory();

        // 2) fetch latest Git tag
        string latestVersion = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

        // 3) download Git source tarball
        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        var downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
        await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

        // 4) install native build dependencies
        _logger.LogInformation("Installing native build dependencies…");
        await _processUtil.ShellRun(InstallScript, tempDir, cancellationToken);

        // 5) extract with fixed timestamps, ownership, order
        _logger.LogInformation("Extracting Git source…");
        await _processUtil.ShellRun($"{ReproEnv} tar --sort=name --mtime=@1620000000 --owner=0 --group=0 --numeric-owner -xzf {archivePath}", tempDir,
            cancellationToken);

        // derive extract path
        string versionTrimmed = latestVersion.TrimStart('v');
        string extractPath = Path.Combine(tempDir, $"git-{versionTrimmed}");

        // 6) generate configure script
        _logger.LogInformation("Generating configure script…");
        await _processUtil.ShellRun($"{ReproEnv} make configure", extractPath, cancellationToken);

        // 7) configure for musl static build with deterministic flags
        _logger.LogInformation("Configuring for musl static build…");
        try
        {
            string ccFlags = $"-ffile-prefix-map={extractPath}=. -fdebug-prefix-map={extractPath}=.";
            string cflags = "-O2 -static -I/usr/include";
            string ldflags = "-static -Wl,--build-id=none";

            string configureCmd =
                $"bash -lc \"CC='musl-gcc {ccFlags}' CFLAGS='{cflags}' LDFLAGS='{ldflags}' ./configure --host=x86_64-linux-musl --prefix=/usr --with-curl --with-openssl --with-expat --with-perl=/usr/bin/perl --with-tcltk\"";
            await _processUtil.ShellRun($"{ReproEnv} {configureCmd}", extractPath, cancellationToken);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(extractPath, "config.log");
            if (File.Exists(logPath))
            {
                var lines = File.ReadLines(logPath).Take(20);
                _logger.LogError("`./configure` failed, first 20 lines of config.log:\n{log}", string.Join(Environment.NewLine, lines));
            }

            throw;
        }

        // 8) compile
        _logger.LogInformation("Compiling Git…");
        await _processUtil.ShellRun($"{ReproEnv} make -C {extractPath} -j{Environment.ProcessorCount}", extractPath, cancellationToken);

        // 9) strip to reduce size
        _logger.LogInformation("Stripping binary…");
        await _processUtil.ShellRun($"{ReproEnv} strip --strip-all git", extractPath, cancellationToken);

        string binaryPath = Path.Combine(extractPath, "git");
        _logger.LogInformation("Static Git binary built at {path}", binaryPath);
        return binaryPath;
    }

    public async ValueTask<string> GetLatestStableGitTag(CancellationToken cancellationToken = default)
    {
        HttpClient client = await _httpClientCache.Get(nameof(BuildLibraryUtil), cancellationToken: cancellationToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetGitTool/1.0");

        JsonElement[]? tags = await client.GetFromJsonAsync<JsonElement[]>("https://api.github.com/repos/git/git/tags", cancellationToken);

        foreach (var tag in tags!)
        {
            string name = tag.GetProperty("name").GetString()!;
            if (!name.Contains("-rc", StringComparison.OrdinalIgnoreCase) && !name.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        throw new InvalidOperationException("No stable Git version found.");
    }
}