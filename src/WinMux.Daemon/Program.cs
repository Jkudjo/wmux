using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinMux.Daemon.Services;

var settings = new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory };
var builder = Host.CreateApplicationBuilder(settings);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// Add Config Support
builder.Configuration.AddJsonFile("config.json", optional: true, reloadOnChange: true);
builder.Services.Configure<WinMux.Daemon.Configuration.WinMuxConfig>(builder.Configuration);

builder.Services.AddHostedService<PipeServerService>();

var host = builder.Build();
await host.RunAsync();
