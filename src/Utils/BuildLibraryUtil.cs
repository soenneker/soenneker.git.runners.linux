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
        // --- Toolchain Configuration ---
        const string toolchainUrl = "https://musl.cc/x86_64-linux-musl-cross.tgz";

        // 1) prepare temp dir
        string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken);
        string toolchainDir = Path.Combine(tempDir, "musl-toolchain");

        // 2) fetch latest Git tag
        string latestVersion = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

        // 3) download Git source tarball
        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        var downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
        await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

        // 4) install HOST build dependencies (make, wget, etc.)
        _logger.LogInformation("Installing native host build dependencies…");
        await _processUtil.BashRun(InstallScript, "", tempDir, cancellationToken);

        // --- NEW STEP: Download and extract the cross-compilation toolchain ---
        _logger.LogInformation("Downloading musl cross-compilation toolchain...");
        string toolchainArchivePath = Path.Combine(tempDir, "musl-toolchain.tgz");
        await _fileDownloadUtil.Download(toolchainUrl, toolchainArchivePath, cancellationToken: cancellationToken);

        _logger.LogInformation("Extracting toolchain...");
        Directory.CreateDirectory(toolchainDir); // Ensure the target directory exists
        await _processUtil.BashRun($"tar -xzf {toolchainArchivePath} -C {toolchainDir} --strip-components=1", "", tempDir, cancellationToken);

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

        // 7) configure for musl static build USING THE TOOLCHAIN
        _logger.LogInformation("Configuring for musl static build using toolchain…");
        try
        {
            // Define paths within the extracted toolchain
            string toolchainBin = Path.Combine(toolchainDir, "bin");
            string toolchainInclude = Path.Combine(toolchainDir, "x86_64-linux-musl/include");
            string toolchainLib = Path.Combine(toolchainDir, "x86_64-linux-musl/lib");

            // Set environment variables to point build tools to the toolchain
            string envVars = $"PATH=\"{toolchainBin}:$PATH\" " +
                             $"CC=x86_64-linux-musl-gcc " +
                             $"CFLAGS='-O2 -static -I{toolchainInclude} -ffile-prefix-map={extractPath}=. -fdebug-prefix-map={extractPath}=.' " +
                             $"LDFLAGS='-static -L{toolchainLib} -Wl,--build-id=none'";

            // Note: The --with-tcltk flag is removed as it's complex to cross-compile.
            // If you don't need git-gui/gitk, it's safer to remove it.
            string configureCmd = $"./configure --host=x86_64-linux-musl --prefix=/usr --with-curl --with-openssl --with-expat --with-perl=/usr/bin/perl --without-tcltk";

            string fullConfigureSnippet = $"{ReproEnv} {envVars} {configureCmd}";
            await _processUtil.BashRun(fullConfigureSnippet, "", extractPath, cancellationToken);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(extractPath, "config.log");
            if (File.Exists(logPath))
            {
                var logContent = await File.ReadAllTextAsync(logPath, cancellationToken);
                _logger.LogError("`./configure` failed. Full content of config.log:\n{log}", logContent);
            }

            throw; // Re-throw the original exception
        }

        // The rest of the build process can use the same environment setup
        _logger.LogInformation("Compiling Git…");
        string toolchainBinForMake = Path.Combine(toolchainDir, "bin");
        string makeEnv = $"{ReproEnv} PATH=\"{toolchainBinForMake}:$PATH\"";
        string compileSnippet = $"{makeEnv} make -j{Environment.ProcessorCount}";
        await _processUtil.BashRun(compileSnippet, "", extractPath, cancellationToken);

        _logger.LogInformation("Stripping binary…");
        string stripEnv = $"{ReproEnv} PATH=\"{toolchainBinForMake}:$PATH\"";
        // Use the strip from the toolchain to be safe
        string stripSnippet = $"{stripEnv} x86_64-linux-musl-strip --strip-all git";
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