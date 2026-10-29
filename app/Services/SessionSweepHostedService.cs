sealed class SessionSweepHostedService : BackgroundService
{
    private readonly KidControlService _service;
    private readonly RuntimeSettings _settings;
    private readonly ILogger<SessionSweepHostedService> _logger;

    public SessionSweepHostedService(KidControlService service, RuntimeSettings settings, ILogger<SessionSweepHostedService> logger)
    {
        _service = service;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Reconcile all managed users immediately on startup and on every pass.
                await _service.SweepExpiredSessionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session sweep failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.SweepIntervalSeconds), stoppingToken);
        }
    }
}
