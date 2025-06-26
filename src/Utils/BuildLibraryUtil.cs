using Microsoft.Extensions.Logging;
using Soenneker.Extensions.ValueTask;
using Soenneker.Git.Runners.Linux.Utils.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Utils.HttpClientCache.Abstract;
using Soenneker.Utils.Process.Abstract;
using System;
using System.IO;
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

    // --- (FIX 1) Remove musl-tools from the install script ---
    private const string InstallScript = "sudo apt-get update && " +
                                         "sudo apt-get install -y build-essential pkg-config libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev tcl-dev tk-dev perl libperl-dev libreadline-dev gettext";

    private readonly ILogger<BuildLibraryUtil> _logger;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IHttpClientCache _httpClientCache;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IProcessUtil _processUtil;
    private readonly IFileUtil _fileUtil;

    public BuildLibraryUtil(ILogger<BuildLibraryUtil> logger, IDirectoryUtil directoryUtil, IHttpClientCache httpClientCache,
        IFileDownloadUtil fileDownloadUtil, IProcessUtil processUtil, IFileUtil fileUtil)
    {
        _logger = logger;
        _directoryUtil = directoryUtil;
        _httpClientCache = httpClientCache;
        _fileDownloadUtil = fileDownloadUtil;
        _processUtil = processUtil;
        _fileUtil = fileUtil;
    }

    public async ValueTask<string> Build(CancellationToken cancellationToken)
    {
        string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken).NoSync();

        string latestVersion = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        var downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
        await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

        _logger.LogInformation("Installing native host build dependencies...");
        await _processUtil.BashRun(InstallScript, "", tempDir, cancellationToken);

        _logger.LogInformation("Extracting Git source…");
        var tarSnippet = $"{ReproEnv} tar --sort=name --mtime=@1620000000 --owner=0 --group=0 --numeric-owner -xzf {archivePath}";
        await _processUtil.BashRun(tarSnippet, "", tempDir, cancellationToken);

        string versionTrimmed = latestVersion.TrimStart('v');
        string extractPath = Path.Combine(tempDir, $"git-{versionTrimmed}");

        _logger.LogInformation("Generating configure script…");
        var makeConfigureSnippet = $"{ReproEnv} make configure";
        await _processUtil.BashRun(makeConfigureSnippet, "", extractPath, cancellationToken);

        // Set up a clean prefix path for install
        string installDir = Path.Combine(tempDir, "git-standalone");

        _logger.LogInformation("Configuring for relocatable build...");
        var configureCmd = $"./configure --prefix={installDir} --with-curl --with-openssl --with-expat --with-perl=/usr/bin/perl --without-tcltk";
        await _processUtil.BashRun($"{ReproEnv} {configureCmd}", "", extractPath, cancellationToken);

        _logger.LogInformation("Compiling Git…");
        var compileSnippet = $"{ReproEnv} make -j{Environment.ProcessorCount}";
        await _processUtil.BashRun(compileSnippet, "", extractPath, cancellationToken);

        _logger.LogInformation("Installing Git to relocatable directory...");
        var installSnippet = $"{ReproEnv} make install";
        await _processUtil.BashRun(installSnippet, "", extractPath, cancellationToken);

        // Copy shared libraries into /lib
        string gitBinPath = Path.Combine(installDir, "bin", "git");
        string libDir = Path.Combine(installDir, "lib");

        _directoryUtil.CreateIfDoesNotExist(libDir);

        _logger.LogInformation("Copying shared library dependencies into {libDir}", libDir);

        var lddCmd = $"ldd {gitBinPath} | grep '=>' | awk '{{print $3}}' | xargs -I '{{}}' cp -u '{{}}' \"{libDir}\"";
        await _processUtil.BashRun(lddCmd, "", tempDir, cancellationToken);

        // Create a wrapper script for standalone execution
        string scriptPath = Path.Combine(installDir, "git.sh");
        var scriptContents =
    $@"#!/bin/bash
DIR=$(dirname ""$(readlink -f ""$0"")"")
export LD_LIBRARY_PATH=""$DIR/lib:$LD_LIBRARY_PATH""
exec ""$DIR/bin/git"" ""$@""";

        await File.WriteAllTextAsync(scriptPath, scriptContents, cancellationToken);
        await _processUtil.BashRun($"chmod +x {scriptPath}", "", tempDir, cancellationToken);

        _logger.LogInformation("Standalone Git folder built at {path}", installDir);
        return installDir;
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