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

    private const string InstallScript = "sudo apt-get update && " + "sudo apt-get install -y build-essential pkg-config autoconf perl gettext " +
                                         "libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev";

    // --- CORRECTION: The crucial flag 'NO_DASHED_BUILTINS' is now included ---
    private const string CommonFlags = "NO_PERL=1 NO_TCLTK=1 NO_PYTHON=1 NO_ICONV=1 NO_GETTEXT=YesPlease " +
                                       "NO_SCALAR=1 NO_SVN=1 NO_P4=1 NO_DAEMON=1 NO_NSEC=1 " +
                                       "NO_DASHED_BUILTINS=YesPlease " + // This is the key to reducing size
                                       "NO_INSTALL_HARDLINKS=YesPlease INSTALL_SYMLINKS=YesPlease";

    private const string BuildFlags = "CFLAGS=\"-Os -ffunction-sections -fdata-sections\" " +
                                      "LDFLAGS=\"-Wl,--gc-sections\"";

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

        // Steps 1 & 2: Download, install deps, extract
        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        await _fileDownloadUtil.Download($"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz", archivePath, cancellationToken: cancellationToken);
        await _processUtil.BashRun(InstallScript, "", tempDir, cancellationToken);
        string tarSnippet = $"{ReproEnv} tar --sort=name --mtime=@1620000000 --owner=0 --group=0 --numeric-owner -xzf {archivePath}";
        await _processUtil.BashRun(tarSnippet, "", tempDir, cancellationToken);
        string extractPath = Path.Combine(tempDir, $"git-{latestVersion.TrimStart('v')}");

        // Step 3: Configure
        _logger.LogInformation("Generating configure script …");
        await _processUtil.BashRun($"{ReproEnv} make configure", "", extractPath, cancellationToken);
        string installDir = Path.Combine(tempDir, "git-standalone");
        _logger.LogInformation("Configuring for slim relocatable build …");
        string configureCmd = $"./configure --prefix={installDir} --with-curl --with-openssl --without-readline --without-tcltk --without-expat";
        await _processUtil.BashRun($"{ReproEnv} {configureCmd}", "", extractPath, cancellationToken);

        // Step 4: Compile & install with correct flags
        _logger.LogInformation("Compiling Git …");
        string compileSnippet = $"{ReproEnv} {BuildFlags} {CommonFlags} make -j{Environment.ProcessorCount}";
        await _processUtil.BashRun(compileSnippet, "", extractPath, cancellationToken);

        _logger.LogInformation("Installing Git (strip & symlink helpers) …");
        string installSnippet = $"{ReproEnv} {BuildFlags} {CommonFlags} INSTALL_STRIP=yes make install";
        await _processUtil.BashRun(installSnippet, "", extractPath, cancellationToken);

        // Step 5: Bundle required shared libraries (excluding system libs)
        string gitBinPath = Path.Combine(installDir, "bin", "git");
        string libDir = Path.Combine(installDir, "lib");
        _directoryUtil.CreateIfDoesNotExist(libDir);
        _logger.LogInformation("Copying shared library dependencies …");
        string lddCmd = $"ldd {gitBinPath} | grep '=>' | awk '{{print $3}}' " +
                        "| grep -vE '(/lib/|/lib64/|/usr/lib/)((ld-linux|libc|libdl|libm|libpthread|librt|libutil|libresolv|libnss|libcrypt).so)' " +
                        "| xargs -I '{{}}' cp -L -u '{{}}' '{libDir}'";
        await _processUtil.BashRun(lddCmd, "", tempDir, cancellationToken);

        // Step 6: Prune unneeded runtime files and helper commands
        _logger.LogInformation("Removing docs, templates, and other superfluous files …");
        string[] toDeleteDirs =
        [
            Path.Combine(installDir, "share", "man"), Path.Combine(installDir, "share", "info"),
            Path.Combine(installDir, "share", "doc"), Path.Combine(installDir, "share", "locale"),
            Path.Combine(installDir, "share", "git-core", "templates"), Path.Combine(installDir, "share", "git-gui"),
            Path.Combine(installDir, "share", "gitk-git"), Path.Combine(installDir, "share", "gitweb"),
            Path.Combine(installDir, "share", "perl5"), Path.Combine(installDir, "share", "bash-completion")
        ];
        foreach (string path in toDeleteDirs.Where(p => Directory.Exists(p) || File.Exists(p)))
        {
            await _processUtil.BashRun($"rm -rf {path}", "", installDir, cancellationToken);
        }

        _logger.LogInformation("Pruning unneeded helper commands ...");
        string[] commandsToDelete =
        [
            "git-sh-i18n", "git-sh-setup", "git-cvsimport", "git-cvsserver", "git-archimport",
            "git-send-email", "git-imap-send", "git-instaweb", "git-p4", "git-svn", "git-daemon"
        ];
        string gitCorePath = Path.Combine(installDir, "libexec", "git-core");
        foreach (string command in commandsToDelete)
        {
            string commandPath = Path.Combine(gitCorePath, command);
            if (File.Exists(commandPath))
            {
                File.Delete(commandPath);
            }
        }

        // Step 7: Create the wrapper script
        string scriptPath = Path.Combine(installDir, "git.sh");
        string scriptContents = "#!/bin/bash\n" + "DIR=$(dirname \"$(readlink -f \"$0\")\")\n" + "export LD_LIBRARY_PATH=\"$DIR/lib:$LD_LIBRARY_PATH\"\n" +
                                "exec \"$DIR/bin/git\" \"$@\"";
        await File.WriteAllTextAsync(scriptPath, scriptContents, cancellationToken);
        await _processUtil.BashRun($"chmod +x {scriptPath}", "", tempDir, cancellationToken);

        _logger.LogInformation("Standalone Git folder built at {path}", installDir);
        return installDir;
    }

    public async ValueTask<string> GetLatestStableGitTag(CancellationToken cancellationToken = default)
    {
        // This method remains unchanged
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