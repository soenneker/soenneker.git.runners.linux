using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Linux.Utils.Abstract;

public interface IFileOperationsUtil
{
    ValueTask Process(string filePath, CancellationToken cancellationToken = default);
}