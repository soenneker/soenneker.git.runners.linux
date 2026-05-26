using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Dotnet.NuGet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.SHA3.Abstract;
using Soenneker.Git.Runners.Linux.Utils.Abstract;

namespace Soenneker.Git.Runners.Linux.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IDotnetNuGetUtil _dotnetNuGetUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IBlake3Util _blake3Util;

    private string? _newHash;

    public FileOperationsUtil(IFileUtil fileUtil, ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IDotnetNuGetUtil dotnetNuGetUtil, IDirectoryUtil directoryUtil, IBlake3Util blake3Util)
    {
        _fileUtil = fileUtil;
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _dotnetNuGetUtil = dotnetNuGetUtil;
        _directoryUtil = directoryUtil;
        _blake3Util = blake3Util;
    }

    public async ValueTask Process(CancellationToken cancellationToken)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}",
            cancellationToken: cancellationToken);

        string targetExePath = Path.Combine(gitDirectory, "src", "Resources", Constants.FileName);

        bool needToUpdate = await CheckForHashDifferences(gitDirectory, filePath, cancellationToken);

        if (!needToUpdate)
            return;

        await BuildPackAndPush(gitDirectory, targetExePath, filePath, cancellationToken);

        await SaveHashToGitRepo(gitDirectory, cancellationToken);
    }

    private async ValueTask BuildPackAndPush(string gitDirectory, string targetExePath, string filePath, CancellationToken cancellationToken)
    {
        await _fileUtil.DeleteIfExists(targetExePath, cancellationToken: cancellationToken);

        await _directoryUtil.CreateIfDoesNotExist(Path.Combine(gitDirectory, "src", "Resources"), cancellationToken: cancellationToken);

        await _fileUtil.Move(filePath, targetExePath, cancellationToken: cancellationToken);

        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string version = EnvironmentUtil.GetVariableStrict("BUILD_VERSION");

        await _dotnetUtil.Pack(projFilePath, version, true, "Release", false, false, gitDirectory, cancellationToken: cancellationToken);

        string apiKey = EnvironmentUtil.GetVariableStrict("NUGET__TOKEN");

        string nuGetPackagePath = Path.Combine(gitDirectory, $"{Constants.Library}.{version}.nupkg");

        await _dotnetNuGetUtil.Push(nuGetPackagePath, apiKey: apiKey, cancellationToken: cancellationToken);
    }

    private async ValueTask<bool> CheckForHashDifferences(string gitDirectory, string filePath, CancellationToken cancellationToken)
    {
        string? oldHash = await _fileUtil.TryRead(Path.Combine(gitDirectory, "hash.txt"), true, cancellationToken);

        if (oldHash == null)
        {
            _logger.LogDebug("Could not read hash from repository, proceeding to update...");
            return true;
        }

        _newHash = await _blake3Util.HashFile(filePath, cancellationToken);

        if (oldHash == _newHash)
        {
            _logger.LogInformation("Hashes are equal, no need to update, exiting...");
            return false;
        }

        return true;
    }

    private async ValueTask SaveHashToGitRepo(string gitDirectory, CancellationToken cancellationToken)
    {
        string targetHashFile = Path.Combine(gitDirectory, "hash.txt");

        await _fileUtil.DeleteIfExists(targetHashFile, cancellationToken: cancellationToken);

        await _fileUtil.Write(targetHashFile, _newHash!, true, cancellationToken);

        await _fileUtil.DeleteIfExists(Path.Combine(gitDirectory, "src", "Resources", Constants.FileName), cancellationToken: cancellationToken);

        await _gitUtil.AddIfNotExists(gitDirectory, targetHashFile, cancellationToken);

        if (await _gitUtil.IsRepositoryDirty(gitDirectory, cancellationToken))
        {
            _logger.LogInformation("Changes have been detected in the repository, commiting and pushing...");

            string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
            string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");
            string token = EnvironmentUtil.GetVariableStrict("GH__TOKEN");

            await _gitUtil.Commit(gitDirectory, "Updates hash for new version", name, email, cancellationToken);

            await _gitUtil.Push(gitDirectory, token, cancellationToken);
        }
        else
        {
            _logger.LogInformation("There are no changes to commit");
        }
    }
}
