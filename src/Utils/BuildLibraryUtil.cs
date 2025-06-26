using Microsoft.Extensions.Logging; using Soenneker.Extensions.Task; using Soenneker.Extensions.ValueTask; using Soenneker.Git.Runners.Linux.Utils.Abstract; using Soenneker.Utils.Directory.Abstract; using Soenneker.Utils.File.Abstract; using Soenneker.Utils.File.Download.Abstract; using Soenneker.Utils.HttpClientCache.Abstract; using Soenneker.Utils.Process.Abstract; using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Net.Http; using System.Net.Http.Json; using System.Text.Json; using System.Threading; using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Linux.Utils;

/// <inheritdoc cref="IBuildLibraryUtil"/> public sealed class BuildLibraryUtil : IBuildLibraryUtil { private const string ReproEnv = "SOURCE_DATE_EPOCH=1620000000 TZ=UTC LC_ALL=C";

// NOTE: gettext was removed; autoconf + perl were added (needed for ./configure generation)
private const string InstallScript =
    "sudo apt-get update && " +
    "sudo apt-get install -y build-essential pkg-config autoconf perl gettext " +
    "libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev";

private const string CommonFlags =
    "NO_PERL=1 " +
    "NO_GETTEXT=YesPlease NO_TCLTK=1 NO_PYTHON=1 NO_ICONV=1 " +
    "SKIP_DASHED_BUILT_INS=YesPlease NO_INSTALL_HARDLINKS=YesPlease INSTALL_SYMLINKS=YesPlease";

private readonly ILogger<BuildLibraryUtil> _logger;
private readonly IDirectoryUtil _directoryUtil;
private readonly IHttpClientCache _httpClientCache;
private readonly IFileDownloadUtil _fileDownloadUtil;
private readonly IProcessUtil _processUtil;
private readonly IFileUtil _fileUtil;

public BuildLibraryUtil(
    ILogger<BuildLibraryUtil> logger,
    IDirectoryUtil directoryUtil,
    IHttpClientCache httpClientCache,
    IFileDownloadUtil fileDownloadUtil,
    IProcessUtil processUtil,
    IFileUtil fileUtil)
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
    // --- STEP 1  Download Git source --------------------------------------------------
    string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken).NoSync();

    string latestVersion = await GetLatestStableGitTag(cancellationToken);
    _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

    string archivePath = Path.Combine(tempDir, "git.tar.gz");
    string downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
    _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
    await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

    // --- STEP 2  Prepare build host ---------------------------------------------------
    _logger.LogInformation("Installing build dependencies ...");
    await _processUtil.BashRun(InstallScript, "", tempDir, cancellationToken);

    _logger.LogInformation("Extracting Git source …");
    string tarSnippet = $"{ReproEnv} tar --sort=name --mtime=@1620000000 --owner=0 --group=0 --numeric-owner -xzf {archivePath}";
    await _processUtil.BashRun(tarSnippet, "", tempDir, cancellationToken);

    string versionTrimmed = latestVersion.TrimStart('v');
    string extractPath = Path.Combine(tempDir, $"git-{versionTrimmed}");

    // --- STEP 3  Configure ------------------------------------------------------------
    _logger.LogInformation("Generating configure script …");
    await _processUtil.BashRun($"{ReproEnv} make configure", "", extractPath, cancellationToken);

    string installDir = Path.Combine(tempDir, "git-standalone");

    _logger.LogInformation("Configuring for slim relocatable build …");
    string configureCmd =
        $"./configure --prefix={installDir} --with-curl --with-openssl " +
        "--without-readline --without-tcltk"; // expat, iconv, gettext disabled via Make flags
    await _processUtil.BashRun($"{ReproEnv} {configureCmd}", "", extractPath, cancellationToken);

    // --- STEP 4  Compile & install ----------------------------------------------------
    _logger.LogInformation("Compiling Git …");
    string compileSnippet = $"{ReproEnv} {CommonFlags} make -j{Environment.ProcessorCount}";
    await _processUtil.BashRun(compileSnippet, "", extractPath, cancellationToken);

    _logger.LogInformation("Installing Git (strip & symlink helpers) …");
    string installSnippet = $"{ReproEnv} {CommonFlags} INSTALL_STRIP=yes make install";
    await _processUtil.BashRun(installSnippet, "", extractPath, cancellationToken);

    // --- STEP 5  Bundle shared libraries ---------------------------------------------
    string gitBinPath = Path.Combine(installDir, "bin", "git");
    string libDir = Path.Combine(installDir, "lib");
    _directoryUtil.CreateIfDoesNotExist(libDir);

    _logger.LogInformation("Copying shared library dependencies …");
    string lddCmd =
        $"ldd {gitBinPath} | grep '=>' | awk '{{print $3}}' | xargs -I '{{}}' cp -L -u '{{}}' '{libDir}'";
    await _processUtil.BashRun(lddCmd, "", tempDir, cancellationToken);

    // --- STEP 6  Prune unneeded runtime files ----------------------------------------
    _logger.LogInformation("Removing docs, templates, and other superfluous files …");
    string[] toDelete =
    {
        Path.Combine(installDir, "share", "man"),
        Path.Combine(installDir, "share", "info"),
        Path.Combine(installDir, "share", "doc"),
        Path.Combine(installDir, "share", "locale"),
        Path.Combine(installDir, "share", "git-core", "templates"),
        Path.Combine(installDir, "share", "git-gui"),
        Path.Combine(installDir, "share", "gitk-git"),
        Path.Combine(installDir, "share", "gitweb"),
        Path.Combine(installDir, "share", "perl5"),
        Path.Combine(installDir, "share", "bash-completion")
    };

    foreach (string path in toDelete)
    {
        if (Directory.Exists(path) || File.Exists(path))
            await _processUtil.BashRun($"rm -rf {path}", "", installDir, cancellationToken);
    }

    // --- STEP 7  Wrapper script -------------------------------------------------------
    string scriptPath = Path.Combine(installDir, "git.sh");
    string scriptContents = "#!/bin/bash\n" +
                           "DIR=$(dirname \"$(readlink -f \"$0\")\")\n" +
                           "export LD_LIBRARY_PATH=\"$DIR/lib:$LD_LIBRARY_PATH\"\n" +
                           "exec \"$DIR/bin/git\" \"$@\"";

    await File.WriteAllTextAsync(scriptPath, scriptContents, cancellationToken);
    await _processUtil.BashRun($"chmod +x {scriptPath}", "", tempDir, cancellationToken);

    _logger.LogInformation("Standalone Git folder built at {path}", installDir);
    return installDir;
}

public async ValueTask<string> GetLatestStableGitTag(CancellationToken cancellationToken = default)
{
    HttpClient client = await _httpClientCache.Get(nameof(BuildLibraryUtil), cancellationToken: cancellationToken);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetGitTool/1.0");

    JsonElement[]? tags =
        await client.GetFromJsonAsync<JsonElement[]>("https://api.github.com/repos/git/git/tags", cancellationToken);

    foreach (JsonElement tag in tags!)
    {
        string name = tag.GetProperty("name").GetString()!;
        if (!name.Contains("-rc", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }
    }

    throw new InvalidOperationException("No stable Git version found.");
}

}

