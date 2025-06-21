using Soenneker.Tests.FixturedUnit;
using Xunit;

namespace Soenneker.Git.Runners.Linux.Tests;

[Collection("Collection")]
public sealed class GitLinuxRunnerTests : FixturedUnitTest
{
    public GitLinuxRunnerTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }

    [Fact]
    public void Default()
    {

    }
}
