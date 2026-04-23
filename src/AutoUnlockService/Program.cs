using AutoUnlockService;

var builder = Host.CreateApplicationBuilder(args);

builder
    .Services
    .Configure<ServiceSettings>(
        builder.Configuration.GetSection("AutoUnlock"));

var host = builder.Build();

await host.RunAsync();
