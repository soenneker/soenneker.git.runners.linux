using Microsoft.Extensions.Logging;
using Soenneker.Extensions.ValueTask;
using Soenneker.Git.Runners.Linux.Utils.Abstract;
using Soenneker.GitHub.Repositories.Tags.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Utils.Process.Abstract;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Linux.Utils;

/// <inheritdoc cref="IBuildLibraryUtil"/>
public sealed class BuildLibraryUtil : IBuildLibraryUtil
{
    private const string _epoch = "1620000000";
    private const string _reproEnv = $"SOURCE_DATE_EPOCH={_epoch} TZ=UTC LC_ALL=C";

    private const string _installScript = "sudo apt-get update && sudo apt-get install -y build-essential pkg-config autoconf perl gettext libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev";

    private readonly ILogger<BuildLibraryUtil> _logger;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IGitHubRepositoriesTagsUtil _tagsUtil;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IProcessUtil _processUtil;

    public BuildLibraryUtil(ILogger<BuildLibraryUtil> logger, IDirectoryUtil directoryUtil, IGitHubRepositoriesTagsUtil tagsUtil,
        IFileDownloadUtil fileDownloadUtil, IProcessUtil processUtil)
    {
        _logger = logger;
        _directoryUtil = directoryUtil;
        _tagsUtil = tagsUtil;
        _fileDownloadUtil = fileDownloadUtil;
        _processUtil = processUtil;
    }

    public async ValueTask<string> Build(CancellationToken cancellationToken)
    {
        string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken).NoSync();
        string tag = await _tagsUtil.GetLatestStableTag("git", "git", cancellationToken);

        _logger.LogInformation("Latest stable Git tag: {tag}", tag);

        string srcTgz = Path.Combine(tempDir, "git.tar.gz");
        await _fileDownloadUtil.Download($"https://github.com/git/git/archive/refs/tags/{tag}.tar.gz", srcTgz, cancellationToken: cancellationToken);

        await _processUtil.BashRun(_installScript, tempDir, cancellationToken: cancellationToken);

        await _processUtil.BashRun($"{_reproEnv} tar --sort=name --mtime=@{_epoch} --owner=0 --group=0 --numeric-owner -xzf git.tar.gz", tempDir,
            cancellationToken: cancellationToken);

        string srcDir = Path.Combine(tempDir, $"git-{tag.TrimStart('v')}");
        string stageDir = Path.Combine(tempDir, "git");

        await _processUtil.BashRun($"{_reproEnv} make configure", srcDir, cancellationToken: cancellationToken);

        // Set reproducible compiler flags
        const string reproducibleEnv = "env SOURCE_DATE_EPOCH=1620000000 TZ=UTC LC_ALL=C " + "CFLAGS=\"-O2 -g0 -frandom-seed=gitbuild\" " +
                                       "LDFLAGS=\"-Wl,--build-id=none\"";

        await _processUtil.BashRun($"{reproducibleEnv} ./configure --prefix=/usr --with-curl --with-openssl --without-readline --without-tcltk", srcDir,
            cancellationToken: cancellationToken);

        const string commonFlags =
            "NO_PERL=1 NO_GETTEXT=YesPlease NO_TCLTK=1 NO_PYTHON=1 NO_ICONV=1 " +
            "SKIP_DASHED_BUILT_INS=YesPlease RUNTIME_PREFIX=YesPlease";

        const string needed = "PROGRAMS='git git-remote-http git-remote-https'";

        // build
        await _processUtil.BashRun($"{reproducibleEnv} {needed} {commonFlags} make -j{Environment.ProcessorCount}", srcDir,
            cancellationToken: cancellationToken);

        // install into stage
        await _processUtil.BashRun($"{reproducibleEnv} {needed} {commonFlags} INSTALL_STRIP=yes make install DESTDIR={stageDir}", srcDir,
            cancellationToken: cancellationToken);

        // ---- ensure git-remote-https is present (AFTER install, BEFORE normalize/strip) ----
        string coreDir = Path.Combine(stageDir, "usr", "libexec", "git-core");
        Directory.CreateDirectory(coreDir); // just in case

        string https = Path.Combine(coreDir, "git-remote-https");
        string http = Path.Combine(coreDir, "git-remote-http");
        string curl = Path.Combine(coreDir, "remote-curl"); // actual helper binary

        if (!File.Exists(https))
        {
            if (File.Exists(http))
            {
                // Size-neutral tiny wrapper
                string wrappera = "#!/bin/sh\nexec \"$(dirname \"$0\")/git-remote-http\" \"$@\"\n";
                await File.WriteAllTextAsync(https, wrappera, cancellationToken);
                await _processUtil.BashRun($"chmod +x \"{https}\"", coreDir, cancellationToken: cancellationToken);
                _logger.LogInformation("Created git-remote-https wrapper -> git-remote-http");
            }
            else if (File.Exists(curl))
            {
                // Byte copy to avoid shell wrapper if you prefer
                await _processUtil.BashRun($"install -m 0755 \"{curl}\" \"{https}\"", coreDir, cancellationToken: cancellationToken);
                _logger.LogInformation("Materialized git-remote-https by copying remote-curl");
            }
            else
            {
                _logger.LogWarning("Neither git-remote-http nor remote-curl found; HTTPS helper may be missing.");
            }
        }
        // -----------------------------------------------------------------------------

        // Debug: check helpers now in the staged tree
        await _processUtil.BashRun("ls -la usr/libexec/git-core/ | grep -E 'git-remote-(http|https)|remote-curl' || true",
            stageDir, cancellationToken: cancellationToken);

        // Normalize timestamps & strip
        await NormalizeTimestamps(stageDir, cancellationToken);
        await StripAllElfFiles(stageDir, cancellationToken);

        // Remove extra files
        string shareDir = Path.Combine(stageDir, "usr", "share");
        if (Directory.Exists(shareDir))
            await _processUtil.BashRun($"rm -rf {shareDir}", stageDir, cancellationToken: cancellationToken);

        // Create wrapper
        string wrapper = Path.Combine(stageDir, "git.sh");
        string script = "#!/bin/bash\n" + "DIR=$(dirname \"$(readlink -f \"$0\")\")\n" + "export LD_LIBRARY_PATH=\"$DIR/lib:$LD_LIBRARY_PATH\"\n" +
                        "export PATH=\"$DIR/usr/libexec/git-core:$PATH\"\n" + "exec \"$DIR/usr/bin/git\" \"$@\"";
        await File.WriteAllTextAsync(wrapper, script, cancellationToken);
        await _processUtil.BashRun($"chmod +x {wrapper}", stageDir, cancellationToken: cancellationToken);

        _logger.LogInformation("Verifying Git HTTPS support…");
        string verifyDir = Path.Combine(tempDir, "clone-test");
        await _processUtil.BashRun($"{wrapper} clone --depth 1 https://github.com/git/git {verifyDir}", tempDir, cancellationToken: cancellationToken);

        _logger.LogInformation("Reproducible Git bundle built at {path}", stageDir);
        return stageDir;
    }

    private async ValueTask NormalizeTimestamps(string dir, CancellationToken ct)
    {
        var cmd = $"find \"{dir}\" -print0 | xargs -0 touch -d @{_epoch}";
        await _processUtil.BashRun(cmd, dir, cancellationToken: ct);
    }

    private async ValueTask StripAllElfFiles(string root, CancellationToken ct)
    {
        string cmd = "find \"" + root + "\" -type f \\( -perm -u+x -o -name '*.so*' \\) " +
                     "-exec sh -c 'file -b \"$1\" | grep -q ELF && strip --strip-unneeded \"$1\"' _ {} \\;";
        await _processUtil.BashRun(cmd, root, cancellationToken: ct);
    }
}