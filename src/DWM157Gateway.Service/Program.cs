using DWM157Gateway.Service;

var options = ServiceOptions.Parse(args);
var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(service => service.ServiceName = "DWM157Gateway")
    .ConfigureServices(services =>
    {
        services.AddSingleton(options);
        services.AddHostedService<GatewayWorker>();
    });

await builder.Build().RunAsync();
