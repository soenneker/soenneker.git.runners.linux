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
using Soenneker.Utils.File.Abstract;

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
    private readonly IFileUtil _fileUtil;

    public BuildLibraryUtil(ILogger<BuildLibraryUtil> logger, IDirectoryUtil directoryUtil, IGitHubRepositoriesTagsUtil tagsUtil,
        IFileDownloadUtil fileDownloadUtil, IProcessUtil processUtil, IFileUtil fileUtil)
    {
        _logger = logger;
        _directoryUtil = directoryUtil;
        _tagsUtil = tagsUtil;
        _fileDownloadUtil = fileDownloadUtil;
        _processUtil = processUtil;
        _fileUtil = fileUtil;
    }

    public async ValueTask<string> Build(CancellationToken cancellationToken)
    {
        // 0) Paths
        string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken).NoSync();
        string tag = await _tagsUtil.GetLatestStableTag("git", "git", cancellationToken);
        _logger.LogInformation("Latest stable Git tag: {tag}", tag);

        string srcTgz = Path.Combine(tempDir, "git.tar.gz");
        await _fileDownloadUtil.Download($"https://github.com/git/git/archive/refs/tags/{tag}.tar.gz", srcTgz, cancellationToken: cancellationToken);

        // 1) Build prerequisites
        await _processUtil.BashRun(_installScript, tempDir, cancellationToken: cancellationToken);

        // 2) Unpack sources
        await _processUtil.BashRun($"{_reproEnv} tar --sort=name --mtime=@{_epoch} --owner=0 --group=0 --numeric-owner -xzf git.tar.gz", tempDir,
            cancellationToken: cancellationToken);

        string srcDir = Path.Combine(tempDir, $"git-{tag.TrimStart('v')}");
        string stageDir = Path.Combine(tempDir, "git");

        // 3) Configure
        await _processUtil.BashRun($"{_reproEnv} make configure", srcDir, cancellationToken: cancellationToken);

        const string reproducibleEnv = "env SOURCE_DATE_EPOCH=1620000000 TZ=UTC LC_ALL=C " + "CFLAGS=\"-O2 -g0 -frandom-seed=gitbuild\" " +
                                       "LDFLAGS=\"-Wl,--build-id=none\"";

        await _processUtil.BashRun($"{reproducibleEnv} ./configure --prefix=/usr --with-curl --with-openssl --without-tcltk", srcDir,
            cancellationToken: cancellationToken);

        // 4) Build minimal set
        const string commonFlags = "NO_PERL=1 NO_GETTEXT=YesPlease NO_TCLTK=1 NO_PYTHON=1 NO_ICONV=1 " +
                                   "SKIP_DASHED_BUILT_INS=YesPlease RUNTIME_PREFIX=YesPlease";
        const string needed = "PROGRAMS='git git-remote-http git-remote-https'";

        await _processUtil.BashRun($"{reproducibleEnv} {needed} {commonFlags} make -j{Environment.ProcessorCount}", srcDir,
            cancellationToken: cancellationToken);

        // 5) Install to stage
        await _processUtil.BashRun($"{reproducibleEnv} {needed} {commonFlags} INSTALL_STRIP=yes make install DESTDIR={stageDir}", srcDir,
            cancellationToken: cancellationToken);

        // 6) Ensure HTTPS helper exists in stage
        string coreDir = Path.Combine(stageDir, "usr", "libexec", "git-core");
        await _directoryUtil.CreateIfDoesNotExist(coreDir, cancellationToken: cancellationToken);

        string https = Path.Combine(coreDir, "git-remote-https");
        string http = Path.Combine(coreDir, "git-remote-http");
        string curl = Path.Combine(coreDir, "remote-curl");

        if (!File.Exists(https))
        {
            if (File.Exists(http))
            {
                const string wrapper = "#!/bin/sh\nexec \"$(dirname \"$0\")/git-remote-http\" \"$@\"\n";
                await File.WriteAllTextAsync(https, wrapper, cancellationToken);
                await _processUtil.BashRun($"chmod +x \"{https}\"", coreDir, cancellationToken: cancellationToken);
            }
            else if (File.Exists(curl))
            {
                await _processUtil.BashRun($"install -m 0755 \"{curl}\" \"{https}\"", coreDir, cancellationToken: cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("Missing remote helpers (git-remote-http/remote-curl); cannot create git-remote-https.");
            }

            await _processUtil.BashRun($"touch -d @{_epoch} \"{https}\"", coreDir, cancellationToken: cancellationToken);
        }

        // Debug in stage
        _logger.LogInformation("Checking what was built and installed (stage)...");
        await _processUtil.BashRun("ls -la usr/libexec/git-core/ | egrep 'git-remote-(http|https)|remote-curl' || true", stageDir,
            cancellationToken: cancellationToken);

        // 7) Repro touches + stripping
        await NormalizeTimestamps(stageDir, cancellationToken);
        await StripAllElfFiles(stageDir, cancellationToken);

        // 8) Trim extras
        string shareDir = Path.Combine(stageDir, "usr", "share");

        if (await _directoryUtil.Exists(shareDir, cancellationToken))
            await _processUtil.BashRun($"rm -rf \"{shareDir}\"", stageDir, cancellationToken: cancellationToken);

        // 9) Launcher (plain)
        string wrapperPath = Path.Combine(stageDir, "git.sh");
        string script = "#!/bin/bash\n" + "set -euo pipefail\n" + "DIR=$(dirname \"$(readlink -f \"$0\")\")\n" +
                        "export LD_LIBRARY_PATH=\"$DIR/lib:${LD_LIBRARY_PATH:-}\"\n" + "export PATH=\"$DIR/usr/libexec/git-core:$PATH\"\n" +
                        "exec \"$DIR/usr/bin/git\" \"$@\"";

        await _fileUtil.Write(wrapperPath, script, true, cancellationToken);
        await _processUtil.BashRun($"chmod +x \"{wrapperPath}\"", stageDir, cancellationToken: cancellationToken);

        // 10) Verify HTTPS from stage
        _logger.LogInformation("Verifying Git HTTPS support (stage)...");
        string verifyDir = Path.Combine(tempDir, "clone-test");
        await _processUtil.BashRun($"{wrapperPath} clone --depth 1 https://github.com/git/git \"{verifyDir}\"", tempDir, cancellationToken: cancellationToken);

        // 11) PUBLISH to the same path your job later uses:
        //     <AppContext.BaseDirectory>/Resources/linux-x64/git
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string resourcesGitDir = Path.Combine(baseDir, "Resources", "linux-x64", "git");
        await _directoryUtil.CreateIfDoesNotExist(resourcesGitDir, cancellationToken: cancellationToken);

        // robust mirror without relying on rsync
        string mirrorCmd = "( set -e; " + $"cd \"{stageDir}\" && tar -cpf - . ) | " + $"( cd \"{resourcesGitDir}\" && tar -xpf - )";
        await _processUtil.BashRun(mirrorCmd, "/", cancellationToken: cancellationToken);

        // Ensure perms survived on https helper and wrapper
        await _processUtil.BashRun("chmod +x usr/libexec/git-core/git-remote-https || true", resourcesGitDir, cancellationToken: cancellationToken);
        await _processUtil.BashRun("chmod +x git.sh", resourcesGitDir, cancellationToken: cancellationToken);

        // 12) Final verify FROM RESOURCES PATH (the one that later fails in your logs)
        _logger.LogInformation("Verifying Git HTTPS support (Resources tree)...");
        await _processUtil.BashRun("./git.sh --version", resourcesGitDir, cancellationToken: cancellationToken);
        await _processUtil.BashRun("./git.sh --exec-path", resourcesGitDir, cancellationToken: cancellationToken);
        await _processUtil.BashRun("ls -la usr/libexec/git-core/ | egrep 'git-remote-(http|https)|remote-curl' || true", resourcesGitDir,
            cancellationToken: cancellationToken);
        string verifyDir2 = Path.Combine(tempDir, "clone-test-resources");
        await _processUtil.BashRun($"./git.sh clone --depth 1 https://github.com/git/git \"{verifyDir2}\"", resourcesGitDir,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Ready bundle at {path}", resourcesGitDir);
        return resourcesGitDir;
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