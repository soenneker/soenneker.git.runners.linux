using Microsoft.Extensions.DependencyInjection;
using Soenneker.Git.Runners.Linux.Utils;
using Soenneker.Git.Runners.Linux.Utils.Abstract;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.Utils.File.Download.Registrars;
using Soenneker.Utils.Paths.Resources.Registrars;

namespace Soenneker.Git.Runners.Linux;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    /// <summary>
    /// Configures services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    /// <summary>
    /// Registers the services required by the application.
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddSingleton<IBuildLibraryUtil, BuildLibraryUtil>()
                .AddFileDownloadUtilAsSingleton()
                .AddResourcesPathUtilAsSingleton()
                .AddRunnersManagerAsSingleton();

        return services;
    }
}
