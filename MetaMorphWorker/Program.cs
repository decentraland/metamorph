using MetaMorphAPI.Utils;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

builder.SetupSerilog();
builder.SetupConverter();
builder.SetupRemoteCache(true);

var app = builder.Build();
app.SetupMetrics();
app.SetupMetrics();
app.Run();