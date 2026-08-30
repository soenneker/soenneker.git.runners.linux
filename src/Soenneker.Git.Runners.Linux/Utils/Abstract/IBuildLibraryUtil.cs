using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Linux.Utils.Abstract;

/// <summary>
/// Builds the Linux Git distribution consumed by the runner.
/// </summary>
public interface IBuildLibraryUtil
{
    /// <summary>
    /// Builds and verifies a distributable Git directory.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The path to the completed distribution directory.</returns>
    ValueTask<string> Build(CancellationToken cancellationToken);
}
