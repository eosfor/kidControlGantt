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
                // Keep persisted sessions and router state in sync even when no UI/API calls happen.
                await _service.SweepExpiredSessionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session sweep failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.SweepIntervalSeconds), stoppingToken);
        }
    }
}
