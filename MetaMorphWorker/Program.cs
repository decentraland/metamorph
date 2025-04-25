using MetaMorphAPI.Utils;

var builder = Host.CreateApplicationBuilder(args);

builder.SetupSerilog();
builder.SetupConverter();
builder.SetupRemoteCache(true);

var host = builder.Build();
host.Run();