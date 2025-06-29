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

    // NOTE: gettext was removed; autoconf + perl were added (needed for ./configure generation)
    private const string InstallScript = "sudo apt-get update && " + "sudo apt-get install -y build-essential pkg-config autoconf perl gettext " +
                                         "libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev";

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
        // ── 1. temp dir + download ─────────────────────────────────────────────────
        string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken).NoSync();
        string tag = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git tag: {tag}", tag);

        string srcTgz = Path.Combine(tempDir, "git.tar.gz");
        await _fileDownloadUtil.Download($"https://github.com/git/git/archive/refs/tags/{tag}.tar.gz", srcTgz, cancellationToken: cancellationToken);

        // ── 2. deps & extract ──────────────────────────────────────────────────────
        await _processUtil.BashRun(InstallScript, tempDir, cancellationToken: cancellationToken);

        await _processUtil.BashRun($"{ReproEnv} tar --sort=name --mtime=@1620000000 " + "--owner=0 --group=0 --numeric-owner -xzf git.tar.gz", tempDir,
            cancellationToken: cancellationToken);

        string srcDir = Path.Combine(tempDir, $"git-{tag.TrimStart('v')}");

        // ── 3. configure (flat prefix + runtime) ───────────────────────────────────
        await _processUtil.BashRun($"{ReproEnv} make configure", srcDir, cancellationToken: cancellationToken);

        string stageDir = Path.Combine(tempDir, "git"); // final bundle root

        await _processUtil.BashRun($"{ReproEnv} ./configure --prefix=/usr --with-curl " + "--with-openssl --without-readline --without-tcltk", srcDir,
            cancellationToken: cancellationToken);

        // ── 4. build & install *only* needed progs ─────────────────────────────────
        const string CommonFlags = "NO_PERL=1 NO_GETTEXT=YesPlease NO_TCLTK=1 NO_PYTHON=1 NO_ICONV=1 " +
                                   "NO_INSTALL_HARDLINKS=YesPlease INSTALL_SYMLINKS=YesPlease " + "RUNTIME_PREFIX=YesPlease";

        const string Needed =
            "PROGRAMS='git git-remote-curl git-remote-http git-remote-https git-remote-ftp git-remote-ftps'"; // installs curl helper + symlinks

        await _processUtil.BashRun($"{ReproEnv} {Needed} {CommonFlags} make -j{Environment.ProcessorCount}", srcDir, cancellationToken: cancellationToken);

        await _processUtil.BashRun($"{ReproEnv} {Needed} {CommonFlags} INSTALL_STRIP=yes make install DESTDIR={stageDir}", srcDir,
            cancellationToken: cancellationToken);

        // ── 5. bundle & strip shared libs ──────────────────────────────────────────
        string gitBin = Path.Combine(stageDir, "usr", "bin", "git");
        string libDir = Path.Combine(stageDir, "lib");
        _directoryUtil.CreateIfDoesNotExist(libDir);

        await _processUtil.BashRun($"ldd {gitBin} | grep '=>' | awk '{{print $3}}' | " + $"xargs -I '{{}}' cp -L -u '{{}}' '{libDir}'", tempDir,
            cancellationToken: cancellationToken);

        await StripAllElfFiles(stageDir, cancellationToken); // strips exes *and* .so’s

        // ── 6. prune leftover cruft (docs, locale, etc.) ───────────────────────────
        string[] junk =
        {
            Path.Combine(stageDir, "share") // entire share dir is now unused
        };
        foreach (string j in junk)
            if (Directory.Exists(j) || File.Exists(j))
                await _processUtil.BashRun($"rm -rf {j}", stageDir, cancellationToken: cancellationToken);

        // ── 7. wrapper (LD_LIBRARY_PATH only) ──────────────────────────────────────
        string wrapper = Path.Combine(stageDir, "git.sh");
        string script = "#!/bin/bash\n" + "DIR=$(dirname \"$(readlink -f \"$0\")\")\n" + "export LD_LIBRARY_PATH=\"$DIR/lib:$LD_LIBRARY_PATH\"\n" +
                        "exec \"$DIR/usr/bin/git\" \"$@\"";
        await File.WriteAllTextAsync(wrapper, script, cancellationToken);
        await _processUtil.BashRun($"chmod +x {wrapper}", stageDir, cancellationToken: cancellationToken);

        _logger.LogInformation("Verifying HTTPS support with a real clone …");

        string verifyDir = Path.Combine(tempDir, "clone-test");
        await _processUtil.BashRun($"{gitBin} clone --depth 1 https://github.com/git/git {verifyDir}", tempDir, cancellationToken: cancellationToken);

        _logger.LogInformation("Slim Git bundle built at {path}", stageDir);
        return stageDir;
    }

    /* ──────────────────────────────────────────────────────────────────────────── */

    /// Strips *all* ELF binaries **and shared libs** under root.
    private async ValueTask StripAllElfFiles(string root, CancellationToken ct)
    {
        string cmd = "find \"" + root + "\" -type f \\( -perm -u+x -o -name '*.so*' \\) " +
                     "-exec sh -c 'file -b \"$1\" | grep -q ELF && strip --strip-unneeded \"$1\"' _ {} \\;";
        await _processUtil.BashRun(cmd, root, cancellationToken: ct);
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