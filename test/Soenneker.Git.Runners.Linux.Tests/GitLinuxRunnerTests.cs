using Soenneker.Tests.HostedUnit;

namespace Soenneker.Git.Runners.Linux.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class GitLinuxRunnerTests : HostedUnitTest
{
    public GitLinuxRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
