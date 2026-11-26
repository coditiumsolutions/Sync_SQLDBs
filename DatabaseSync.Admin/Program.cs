using System.Security.Claims;
using DatabaseSync.Admin.Services;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

// Bind to all interfaces so the Admin UI is reachable on the server LAN/VPN.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5088");

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Cookie.Name = "Coditium.SyncAdmin";
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IWorkerServiceControl, WorkerServiceControl>();
builder.Services.AddSingleton<ManualSyncHost>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddDatabaseSyncInfrastructure(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var management = scope.ServiceProvider.GetRequiredService<IManagementStore>();
    await management.InitializeAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
