using Microsoft.Extensions.Logging;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Git.Runners.Linux.Utils.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Utils.HttpClientCache.Abstract;
using Soenneker.Utils.Process.Abstract;
using System;
using System.Collections.Generic;
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
                                         "sudo apt-get install -y build-essential pkg-config libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev gettext";

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
        // --- STEP 1: Setup and Download ---
        string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken).NoSync();

        string latestVersion = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        var downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
        await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

        // --- STEP 2: Extract and Build ---
        _logger.LogInformation("Installing native host build dependencies...");
        //
        // Correction 1: Ensure Perl is included in your InstallScript for the build to succeed.
        // Your `InstallScript` variable (which is not shown here) must contain a command like:
        // "apt-get update && apt-get install -y build-essential libssl-dev libcurl4-openssl-dev libexpat1-dev gettext zlib1g-dev autoconf perl"
        // The key is adding `perl` to the list of packages.
        //
        await _processUtil.BashRun(InstallScript, "", tempDir, cancellationToken);

        _logger.LogInformation("Extracting Git source…");
        var tarSnippet = $"{ReproEnv} tar --sort=name --mtime=@1620000000 --owner=0 --group=0 --numeric-owner -xzf {archivePath}";
        await _processUtil.BashRun(tarSnippet, "", tempDir, cancellationToken);

        string versionTrimmed = latestVersion.TrimStart('v');
        string extractPath = Path.Combine(tempDir, $"git-{versionTrimmed}");

        _logger.LogInformation("Generating configure script…");
        var makeConfigureSnippet = $"{ReproEnv} make configure";
        await _processUtil.BashRun(makeConfigureSnippet, "", extractPath, cancellationToken);

        string installDir = Path.Combine(tempDir, "git-standalone");

        _logger.LogInformation("Configuring for slim relocatable build...");
        var configureCmd = $"./configure --prefix={installDir} --with-curl --with-openssl --with-expat --without-readline --without-tcltk";
        await _processUtil.BashRun($"{ReproEnv} {configureCmd}", "", extractPath, cancellationToken);

        // Correction 2: Use NO_PERL=1 to prevent Perl-based commands and libraries from being installed.
        // This reduces the final package size and removes the runtime dependency.
        _logger.LogInformation("Compiling Git without Perl dependencies…");
        var compileSnippet = $"{ReproEnv} NO_PERL=1 NO_GETTEXT=YesPlease NO_TCLTK=1 " +
                              $"NO_INSTALL_HARDLINKS=YesPlease SKIP_DASHED_BUILT_INS=YesPlease " +
                              $"make -j{Environment.ProcessorCount}";
        await _processUtil.BashRun(compileSnippet, "", extractPath, cancellationToken);

        _logger.LogInformation("Installing Git...");
        var installSnippet = $"{ReproEnv} NO_PERL=1 NO_GETTEXT=YesPlease NO_TCLTK=1 " +
                              $"NO_INSTALL_HARDLINKS=YesPlease SKIP_DASHED_BUILT_INS=YesPlease " +
                              $"INSTALL_STRIP=yes make install";
        await _processUtil.BashRun(installSnippet, "", extractPath, cancellationToken);

        // --- STEP 4: Create Standalone Package (Stripping, etc.) ---
        // (The rest of the code remains the same as the previous correct version)

        string gitBinPath = Path.Combine(installDir, "bin", "git");
        string libDir = Path.Combine(installDir, "lib");
        string libexecDir = Path.Combine(installDir, "libexec", "git-core");

        _directoryUtil.CreateIfDoesNotExist(libDir);

        _logger.LogInformation("Copying shared library dependencies into {libDir}", libDir);
        var lddCmd = $"ldd {gitBinPath} | grep '=>' | awk '{{print $3}}' | xargs -I '{{}}' cp -L -u '{{}}' \"{libDir}\"";
        await _processUtil.BashRun(lddCmd, "", tempDir, cancellationToken);

        _logger.LogInformation("Stripping Git binary, helpers, and shared libraries...");
        await _processUtil.BashRun($"strip {gitBinPath}", "", installDir, cancellationToken);

        var stripExecutablesCmd = $"find . -type f -exec file {{}} + | grep -E 'ELF (executable|shared object)' " +
                                   $"| cut -d: -f1 | xargs --no-run-if-empty strip --strip-unneeded";

        if (Directory.Exists(libexecDir))
        {
            _logger.LogInformation("Stripping helper executables in {libexecDir}...", libexecDir);
            await _processUtil.BashRun(stripExecutablesCmd, "", libexecDir, cancellationToken);
        }

        if (Directory.Exists(libDir) && Directory.GetFiles(libDir).Any())
        {
            _logger.LogInformation("Stripping shared libraries in {libDir}...", libDir);
            await _processUtil.BashRun(stripExecutablesCmd, "", libDir, cancellationToken);
        }

        _logger.LogInformation("Removing unnecessary directories to minimize size...");
        var unnecessaryShareItems = new[]
        {
            "doc", "git-gui", "gitk-git", "locale", "gitweb", "perl5", "bash-completion"
        };

        foreach (string item in unnecessaryShareItems)
        {
            string itemPath = Path.Combine(installDir, "share", item);
            if (Directory.Exists(itemPath) || File.Exists(itemPath))
            {
                await _processUtil.BashRun($"rm -rf {itemPath}", "", installDir, cancellationToken);
            }
        }

        string scriptPath = Path.Combine(installDir, "git.sh");
        var scriptContents = $@"#!/bin/bash
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

        foreach (JsonElement tag in tags!)
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