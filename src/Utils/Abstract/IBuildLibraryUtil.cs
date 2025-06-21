using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Linux.Utils.Abstract;

public interface IBuildLibraryUtil
{
    ValueTask<string> Build(CancellationToken cancellationToken);
}