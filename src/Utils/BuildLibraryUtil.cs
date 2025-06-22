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
        string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken);

        // 2) fetch latest Git tag
        string latestVersion = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

        // 3) download Git source tarball
        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        var downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
        await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

        // 4) install HOST build dependencies (make, wget, etc.)
        _logger.LogInformation("Installing native host build dependencies, including musl-tools…");
        await _processUtil.BashRun(InstallScript, "", tempDir, cancellationToken);

        // 5) extract Git source
        _logger.LogInformation("Extracting Git source…");
        string tarSnippet = $"{ReproEnv} tar --sort=name --mtime=@1620000000 --owner=0 --group=0 --numeric-owner -xzf {archivePath}";
        await _processUtil.BashRun(tarSnippet, "", tempDir, cancellationToken);

        string versionTrimmed = latestVersion.TrimStart('v');
        string extractPath = Path.Combine(tempDir, $"git-{versionTrimmed}");

        // 6) generate configure script
        _logger.LogInformation("Generating configure script…");
        string makeConfigureSnippet = $"{ReproEnv} make configure";
        await _processUtil.BashRun(makeConfigureSnippet, "", extractPath, cancellationToken);

        // 7) configure for musl static build
        _logger.LogInformation("Configuring for musl static build using system toolchain…");

        // --- FIX IS HERE ---
        // We must tell the musl-gcc compiler where to find the system's (glibc) dev libraries.
        // -I adds a directory to the header search path.
        // -L adds a directory to the library search path.
        // The paths are standard for Debian/Ubuntu-based systems.
        const string includePath = "/usr/include/x86_64-linux-gnu";
        const string libPath = "/usr/lib/x86_64-linux-gnu";

        string envVars = $"CC=musl-gcc " +
                         $"STRIP=x86_64-linux-musl-strip " +
                         $"CFLAGS='-I{includePath} -O2 -static -ffile-prefix-map={extractPath}=. -fdebug-prefix-map={extractPath}=.' " +
                         $"LDFLAGS='-L{libPath} -static -Wl,--build-id=none'";

        string configureCmd = $"./configure --host=x86_64-linux-musl --prefix=/usr --with-curl --with-openssl --with-expat --with-perl=/usr/bin/perl --without-tcltk";

        string fullConfigureSnippet = $"{ReproEnv} {envVars} {configureCmd}";
        await _processUtil.BashRun(fullConfigureSnippet, "", extractPath, cancellationToken);

        // Compile step remains the same
        _logger.LogInformation("Compiling Git…");
        string compileSnippet = $"{ReproEnv} make -j{Environment.ProcessorCount}";
        await _processUtil.BashRun(compileSnippet, "", extractPath, cancellationToken);

        // NOW, use `make` to strip the binary using the configured STRIP variable
        _logger.LogInformation("Stripping binary…");
        string stripSnippet = $"{ReproEnv} make strip";
        await _processUtil.BashRun(stripSnippet, "", extractPath, cancellationToken);

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