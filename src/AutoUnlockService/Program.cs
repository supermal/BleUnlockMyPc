using AutoUnlockService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "AutoUnlockService")
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<ServiceSettings>(ctx.Configuration.GetSection("AutoUnlock"));
        services.AddSingleton<PipeServer>();
        services.AddSingleton<CredentialStore>();
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
