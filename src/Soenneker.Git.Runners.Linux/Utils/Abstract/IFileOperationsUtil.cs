using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Linux.Utils.Abstract;

/// <summary>
/// Defines the file operations util contract.
/// </summary>
public interface IFileOperationsUtil
{
    /// <summary>
    /// Executes the process operation.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask Process(string filePath, CancellationToken cancellationToken);
}
