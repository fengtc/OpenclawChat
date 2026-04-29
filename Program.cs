using OpenclawChat.Services;
using OpenclawChat.Models;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor()
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = 2 * 1024 * 1024;
    });
builder.Services.Configure<OpenclawConnectionOptions>(builder.Configuration.GetSection("OpenclawConnection"));
builder.Services.AddScoped<OpenclawWsClient>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".data-protection-keys")));

var dbPath = builder.Configuration["UserStore:DatabasePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "openclaw-chat.db");
builder.Services.AddSingleton<UserStore>(_ => new UserStore(dbPath));
builder.Services.AddScoped<AuthState>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
