using MetaMorphAPI.Utils;

var builder = Host.CreateApplicationBuilder(args);

builder.SetupSerilog();
builder.SetupConverter();
builder.SetupRemoteCache();

var host = builder.Build();
host.Run();