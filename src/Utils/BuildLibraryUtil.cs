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

    private const string CommonFlags = "NO_PERL=1 " + "NO_GETTEXT=YesPlease NO_TCLTK=1 NO_PYTHON=1 NO_ICONV=1 " +
                                       "SKIP_DASHED_BUILT_INS=YesPlease NO_INSTALL_HARDLINKS=YesPlease INSTALL_SYMLINKS=YesPlease";

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
        // ── STEP 1 – temp dir + download ────────────────────────────────────────────
        string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken).NoSync();

        string latestTag = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git tag: {tag}", latestTag);

        string srcTgz = Path.Combine(tempDir, "git.tar.gz");
        await _fileDownloadUtil.Download(
            $"https://github.com/git/git/archive/refs/tags/{latestTag}.tar.gz",
            srcTgz, cancellationToken: cancellationToken);

        // ── STEP 2 – deps + extract ─────────────────────────────────────────────────
        await _processUtil.BashRun(InstallScript, tempDir, cancellationToken: cancellationToken);

        string tarCmd = $"{ReproEnv} tar --sort=name --mtime=@1620000000 " +
                        "--owner=0 --group=0 --numeric-owner -xzf git.tar.gz";
        await _processUtil.BashRun(tarCmd, tempDir, cancellationToken: cancellationToken);

        string srcDir = Path.Combine(tempDir, $"git-{latestTag.TrimStart('v')}");

        // ── STEP 3 – configure  (prefix=/usr  +  runtime-prefix) ────────────────────
        await _processUtil.BashRun($"{ReproEnv} make configure", srcDir, cancellationToken: cancellationToken);

        string stageDir = Path.Combine(tempDir, "git");   // bundle root (will contain usr/…)

        string configureCmd =
            "./configure --prefix=/usr --enable-runtime-prefix " +      // ← key change
            "--with-curl --with-openssl --without-readline --without-tcltk";
        await _processUtil.BashRun($"{ReproEnv} {configureCmd}", srcDir, cancellationToken: cancellationToken);

        // ── STEP 4 – build & install  (DESTDIR = stageDir) ──────────────────────────
        const string CommonFlags =   // rebuild w/out SKIP_DASHED_BUILT_INS so helpers install
            "NO_PERL=1 NO_GETTEXT=YesPlease NO_TCLTK=1 NO_PYTHON=1 NO_ICONV=1 " +
            "NO_INSTALL_HARDLINKS=YesPlease INSTALL_SYMLINKS=YesPlease";

        string buildCmd = $"{ReproEnv} {CommonFlags} make -j{Environment.ProcessorCount}";
        await _processUtil.BashRun(buildCmd, srcDir, cancellationToken: cancellationToken);

        string installCmd = $"{ReproEnv} {CommonFlags} INSTALL_STRIP=yes make install DESTDIR={stageDir}";
        await _processUtil.BashRun(installCmd, srcDir, cancellationToken: cancellationToken);

        // ── STEP 5 – bundle shared libs ─────────────────────────────────────────────
        string gitBin = Path.Combine(stageDir, "usr", "bin", "git");
        string libDir = Path.Combine(stageDir, "usr", "lib");
        _directoryUtil.CreateIfDoesNotExist(libDir);

        string lddCmd =
            $"ldd {gitBin} | grep '=>' | awk '{{print $3}}' | " +
            $"xargs -I '{{}}' cp -L -u '{{}}' '{libDir}'";
        await _processUtil.BashRun(lddCmd, tempDir, cancellationToken: cancellationToken);

        await StripAllExecutables(stageDir, cancellationToken);

        // ── STEP 6 – prune docs/locales/etc. (paths under usr/) ─────────────────────
        string[] prune =
        {
        Path.Combine(stageDir, "usr", "share", "man"),
        Path.Combine(stageDir, "usr", "share", "info"),
        Path.Combine(stageDir, "usr", "share", "doc"),
        Path.Combine(stageDir, "usr", "share", "locale"),
        Path.Combine(stageDir, "usr", "share", "git-core", "templates"),
        Path.Combine(stageDir, "usr", "share", "git-gui"),
        Path.Combine(stageDir, "usr", "share", "gitk-git"),
        Path.Combine(stageDir, "usr", "share", "gitweb"),
        Path.Combine(stageDir, "usr", "share", "perl5"),
        Path.Combine(stageDir, "usr", "share", "bash-completion")
    };
        foreach (string p in prune)
            if (Directory.Exists(p) || File.Exists(p))
                await _processUtil.BashRun($"rm -rf {p}", stageDir, cancellationToken: cancellationToken);

        // ── STEP 7 – drop helpers you never use (still leave remote-https et al.) ──
        string coreDir = Path.Combine(stageDir, "usr", "libexec", "git-core");
        string[] drop =
        {
        "git-remote-ftp", "git-remote-ftps", "git-daemon",
        "git-cvsimport",  "git-cvsserver",   "git-archimport",
        "git-svn",        "git-p4",          "git-web--browse",
        "git-instaweb",   "git-send-email",  "git-imap-send"
    };
        foreach (string h in drop)
        {
            string hp = Path.Combine(coreDir, h);
            if (File.Exists(hp)) File.Delete(hp);
        }

        // ── STEP 8 – wrapper (only sets LD_LIBRARY_PATH) ────────────────────────────
        string wrapperPath = Path.Combine(stageDir, "git.sh");
        string wrapper =
            "#!/bin/bash\n" +
            "DIR=$(dirname \"$(readlink -f \"$0\")\")\n" +
            "export LD_LIBRARY_PATH=\"$DIR/usr/lib:$LD_LIBRARY_PATH\"\n" +
            "exec \"$DIR/usr/bin/git\" \"$@\"";
        await File.WriteAllTextAsync(wrapperPath, wrapper, cancellationToken);
        await _processUtil.BashRun($"chmod +x {wrapperPath}", stageDir, cancellationToken: cancellationToken);

        _logger.LogInformation("Standalone Git built at {path}", stageDir);
        return stageDir;                    // copy this whole ‘git/’ folder wherever you like
    }

    private async ValueTask StripAllExecutables(string root, CancellationToken ct)
    {
        // Finds every executable file under <root>, checks if it is an ELF,
        // then strips unneeded sections & symbols in-place.
        //
        //   find  <root>  -type f -executable \
        //     -exec sh -c 'file -b "$1" | grep -q ELF && strip --strip-unneeded "$1"' _ {} \;
        //
        string stripCmd = $"find \"{root}\" -type f -executable " + $"-exec sh -c 'file -b \"$1\" | grep -q ELF && strip --strip-unneeded \"$1\"' _ {{}} \\;";

        await _processUtil.BashRun(stripCmd, root, cancellationToken: ct);
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