using DatabaseSync.Admin.Services;
using DatabaseSync.Infrastructure;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

// Bind to all interfaces so the Admin UI is reachable on the server LAN/VPN.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5088");

builder.Services.AddRazorPages();
builder.Services.AddSingleton<IWorkerServiceControl, WorkerServiceControl>();
builder.Services.AddDatabaseSyncInfrastructure(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

app.Run();
