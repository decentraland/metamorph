using MetaMorphAPI.Utils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsProduction())
{
    builder.WebHost.UseSentry();
}

builder.Services.AddHttpClient();

builder.SetupSerilog();
builder.SetupConverter();
builder.SetupRemoteCache(true, false);
builder.SetupHealthChecks();

var app = builder.Build();
app.SetupMetrics();
app.SetupHealthCheck();
app.Run();