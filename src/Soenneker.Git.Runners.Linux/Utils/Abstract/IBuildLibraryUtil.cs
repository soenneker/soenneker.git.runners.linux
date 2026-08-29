using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Linux.Utils.Abstract;

/// <summary>
/// Defines the build library util contract.
/// </summary>
public interface IBuildLibraryUtil
{
    /// <summary>
    /// Builds build Library.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the text returned by build.</returns>
    ValueTask<string> Build(CancellationToken cancellationToken);
}
