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

///<inheritdoc cref="IBuildLibraryUtil"/>
public sealed class BuildLibraryUtil : IBuildLibraryUtil
{
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
        string tempDir = _directoryUtil.CreateTempDirectory();

        string latestVersion = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        var downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";

        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
        _ = await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

        _logger.LogInformation("Installing native build dependencies…");
        var installScript =
            "sudo apt-get update && " +
            "sudo apt-get install -y " +
            "build-essential " +
            "musl-tools " +
            "pkg-config " +
            "libcurl4-openssl-dev " +
            "libssl-dev " +
            "libexpat1-dev " +
            "zlib1g-dev " +
            "tcl-dev " +
            "tk-dev " +
            "perl " +
            "libperl-dev " +
            "libreadline-dev"+
            "gettext";

        // single-quoted inside -lc so bash sees it all as one command
        await _processUtil.BashRun(
            "bash",
            $"-lc '{installScript}'",
            tempDir,
            cancellationToken
        );

        await _processUtil.BashRun("tar", $"-xzf {archivePath}", tempDir, cancellationToken);

        string versionTrimmed = latestVersion.TrimStart('v');
        string extractPath = Path.Combine(tempDir, $"git-{versionTrimmed}");

        // 1) generate configure
        await _processUtil.BashRun("make", "configure", extractPath, cancellationToken);

        // 2) configure for musl static everything
        try
        {
            await _processUtil.BashRun(
                "bash",
                $"-lc \"./configure " +
                "--host=x86_64-linux-musl " +
                "--prefix=/usr " +
                "--with-curl --with-openssl --with-expat --with-perl=/usr/bin/perl --with-tcltk " +
                "CC=musl-gcc CFLAGS='-O2 -static -I/usr/include' LDFLAGS='-static'\"",
                extractPath,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(extractPath, "config.log");
            if (File.Exists(logPath))
            {
                var lines = File.ReadLines(logPath).Take(20);
                _logger.LogError("`./configure` failed, first 20 lines of config.log:\n{log}",
                    string.Join(Environment.NewLine, lines));
            }
            throw; // re-throw so upstream sees the failure
        }

        // 3) compile in parallel
        await _processUtil.BashRun("make", $"-j{Environment.ProcessorCount}", extractPath, cancellationToken);

        // 4) strip to reduce size
        await _processUtil.BashRun("strip", "git", extractPath, cancellationToken);

        string binaryPath = Path.Combine(extractPath, "git");
        _logger.LogInformation("Static Git binary built at {path}", binaryPath);

        return binaryPath;
    }

    public async ValueTask<string> GetLatestStableGitTag(CancellationToken cancellationToken = default)
    {
        HttpClient client = await _httpClientCache.Get(nameof(BuildLibraryUtil), cancellationToken: cancellationToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetGitTool/1.0");

        JsonElement[]? tags = await client.GetFromJsonAsync<JsonElement[]>("https://api.github.com/repos/git/git/tags", cancellationToken);

        foreach (JsonElement tag in tags)
        {
            string name = tag.GetProperty("name").GetString()!;
            if (!name.Contains("-rc", StringComparison.OrdinalIgnoreCase) && !name.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
                return name;
        }

        throw new Exception("No stable Git version found.");
    }
}