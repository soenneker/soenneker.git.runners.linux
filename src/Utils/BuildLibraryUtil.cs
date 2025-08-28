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

    private const string _installScript =
        "sudo apt-get update && sudo apt-get install -y build-essential pkg-config autoconf perl gettext libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev";

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

        // Reproducible compiler/linker flags
        const string reproducibleEnv = "env SOURCE_DATE_EPOCH=1620000000 TZ=UTC LC_ALL=C " + "CFLAGS=\"-O2 -g0 -frandom-seed=gitbuild\" " +
                                       "LDFLAGS=\"-Wl,--build-id=none\"";

        await _processUtil.BashRun($"{reproducibleEnv} ./configure --prefix=/usr --with-curl --with-openssl --without-tcltk", srcDir,
            cancellationToken: cancellationToken);

        // Lean build; no special hardlink/symlink flags
        const string commonFlags = "NO_PERL=1 NO_GETTEXT=YesPlease NO_TCLTK=1 NO_PYTHON=1 NO_ICONV=1 " +
                                   "SKIP_DASHED_BUILT_INS=YesPlease RUNTIME_PREFIX=YesPlease";

        const string needed = "PROGRAMS='git git-remote-http git-remote-https'";

        await _processUtil.BashRun($"{reproducibleEnv} {needed} {commonFlags} make -j{Environment.ProcessorCount}", srcDir,
            cancellationToken: cancellationToken);

        // Install into stage
        await _processUtil.BashRun($"{reproducibleEnv} {needed} {commonFlags} INSTALL_STRIP=yes make install DESTDIR={stageDir}", srcDir,
            cancellationToken: cancellationToken);

        // --- Guarantee HTTPS helper exists in the staged tree ---
        string coreDir = Path.Combine(stageDir, "usr", "libexec", "git-core");
        Directory.CreateDirectory(coreDir);
        string https = Path.Combine(coreDir, "git-remote-https");
        string http = Path.Combine(coreDir, "git-remote-http");
        string curl = Path.Combine(coreDir, "remote-curl");

        if (!File.Exists(https))
        {
            if (File.Exists(http))
            {
                // Size-neutral wrapper; survives any copy tooling
                string wrapper = "#!/bin/sh\nexec \"$(dirname \"$0\")/git-remote-http\" \"$@\"\n";
                await File.WriteAllTextAsync(https, wrapper, cancellationToken);
                await _processUtil.BashRun($"chmod +x \"{https}\"", coreDir, cancellationToken: cancellationToken);
                await _processUtil.BashRun($"touch -d @{_epoch} \"{https}\"", coreDir, cancellationToken: cancellationToken);
                _logger.LogInformation("Created git-remote-https wrapper -> git-remote-http in stage.");
            }
            else if (File.Exists(curl))
            {
                // Fallback to copying payload if http alias wasn't materialized
                await _processUtil.BashRun($"install -m 0755 \"{curl}\" \"{https}\"", coreDir, cancellationToken: cancellationToken);
                await _processUtil.BashRun($"touch -d @{_epoch} \"{https}\"", coreDir, cancellationToken: cancellationToken);
                _logger.LogInformation("Materialized git-remote-https by copying remote-curl in stage.");
            }
            else
            {
                _logger.LogWarning("Neither git-remote-http nor remote-curl found; HTTPS helper may be missing.");
            }
        }
        // --------------------------------------------------------

        // Debug: confirm helpers in stage
        _logger.LogInformation("Checking what was built and installed...");
        await _processUtil.BashRun("ls -la usr/libexec/git-core/ | egrep 'git-remote-(http|https)|remote-curl' || true", stageDir,
            cancellationToken: cancellationToken);
        await _processUtil.BashRun("ls -la usr/bin/", stageDir, cancellationToken: cancellationToken);

        // Normalize timestamps to SOURCE_DATE_EPOCH
        await NormalizeTimestamps(stageDir, cancellationToken);

        // Strip ELF files
        await StripAllElfFiles(stageDir, cancellationToken);

        // Remove extra files we don’t ship
        string shareDir = Path.Combine(stageDir, "usr", "share");
        if (Directory.Exists(shareDir))
            await _processUtil.BashRun($"rm -rf \"{shareDir}\"", stageDir, cancellationToken: cancellationToken);

        // Create launcher with self-healing for git-remote-https
        string wrapperPath = Path.Combine(stageDir, "git.sh");
        string script = "#!/bin/bash\n" + "set -euo pipefail\n" + "DIR=$(dirname \"$(readlink -f \"$0\")\")\n" + "core=\"$DIR/usr/libexec/git-core\"\n" + "\n" +
                        "# Self-heal if copy/publish dropped the https helper\n" + "if [ ! -x \"$core/git-remote-https\" ]; then\n" +
                        "  if [ -x \"$core/git-remote-http\" ]; then\n" +
                        "    printf '#!/bin/sh\\nexec \"$(dirname \"$0\")/git-remote-http\" \"$@\"\\n' > \"$core/git-remote-https\"\n" +
                        "    chmod +x \"$core/git-remote-https\"\n" + "    touch -d @" + _epoch + " \"$core/git-remote-https\" || true\n" +
                        "  elif [ -x \"$core/remote-curl\" ]; then\n" + "    install -m 0755 \"$core/remote-curl\" \"$core/git-remote-https\"\n" +
                        "    touch -d @" + _epoch + " \"$core/git-remote-https\" || true\n" + "  fi\n" + "fi\n" + "\n" +
                        "export LD_LIBRARY_PATH=\"$DIR/lib:${LD_LIBRARY_PATH:-}\"\n" + "export PATH=\"$core:$PATH\"\n" + "exec \"$DIR/usr/bin/git\" \"$@\"";
        await File.WriteAllTextAsync(wrapperPath, script, cancellationToken);
        await _processUtil.BashRun($"chmod +x \"{wrapperPath}\"", stageDir, cancellationToken: cancellationToken);

        _logger.LogInformation("Verifying Git HTTPS support…");
        string verifyDir = Path.Combine(tempDir, "clone-test");
        await _processUtil.BashRun($"{wrapperPath} clone --depth 1 https://github.com/git/git \"{verifyDir}\"", tempDir, cancellationToken: cancellationToken);

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