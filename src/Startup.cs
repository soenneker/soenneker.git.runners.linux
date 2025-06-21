using Microsoft.Extensions.DependencyInjection;
using Soenneker.Git.Runners.Linux.Utils;
using Soenneker.Git.Runners.Linux.Utils.Abstract;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.Utils.File.Download.Registrars;

namespace Soenneker.Git.Runners.Linux;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddScoped<IFileOperationsUtil, FileOperationsUtil>()
                .AddScoped<IBuildLibraryUtil, BuildLibraryUtil>()
                .AddFileDownloadUtilAsScoped()
                .AddRunnersManagerAsScoped();

        return services;
    }
}