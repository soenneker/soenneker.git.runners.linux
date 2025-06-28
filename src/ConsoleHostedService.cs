using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Git.Runners.Linux.Utils.Abstract;
using Soenneker.Managers.Runners.Abstract;

namespace Soenneker.Git.Runners.Linux;

public sealed class ConsoleHostedService : IHostedService
{
    private readonly ILogger<ConsoleHostedService> _logger;

    private readonly IHostApplicationLifetime _appLifetime;
    private readonly IRunnersManager _runnersManager;
    private readonly IBuildLibraryUtil _buildLibraryUtil;

    private int? _exitCode;

    public ConsoleHostedService(ILogger<ConsoleHostedService> logger, IHostApplicationLifetime appLifetime, IRunnersManager runnersManager,
        IBuildLibraryUtil buildLibraryUtil)
    {
        _logger = logger;
        _appLifetime = appLifetime;
        _runnersManager = runnersManager;
        _buildLibraryUtil = buildLibraryUtil;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _appLifetime.ApplicationStarted.Register(() =>
        {
            Task.Run(async () =>
            {
                _logger.LogInformation("Running console hosted service ...");

                try
                {
                    string installDir = await _buildLibraryUtil.Build(cancellationToken);

                    await _runnersManager.PushIfChangesNeededForDirectory(Path.Combine("linux-x64", "git"), installDir, Constants.Library,
                        $"https://github.com/soenneker/{Constants.Library}", cancellationToken);

                    _logger.LogInformation("Complete!");

                    _exitCode = 0;
                }
                catch (Exception e)
                {
                    if (Debugger.IsAttached)
                        Debugger.Break();

                    _logger.LogError(e, "Unhandled exception");

                    await Task.Delay(2000, cancellationToken);
                    _exitCode = 1;
                }
                finally
                {
                    // Stop the application once the work is done
                    _appLifetime.StopApplication();
                }
            }, cancellationToken);
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Exiting with return code: {exitCode}", _exitCode);

        // Exit code may be null if the user cancelled via Ctrl+C/SIGTERM
        Environment.ExitCode = _exitCode.GetValueOrDefault(-1);
        return Task.CompletedTask;
    }
}