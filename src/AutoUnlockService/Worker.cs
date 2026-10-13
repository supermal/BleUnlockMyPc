namespace AutoUnlockService;

// Por ahora solo late; en la Task 15 se convierte en el orquestador real
public sealed class Worker(ILogger<Worker> log, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Worker 已启动");
        while (!stoppingToken.IsCancellationRequested)
        {
            log.LogInformation("心跳：服务运行中");
            await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
        }
    }
}
