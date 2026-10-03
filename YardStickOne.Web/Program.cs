using YardStickOne.Web.Components;
using YardStickOne.Web.Hubs;
using YardStickOne.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();
builder.Services.AddSingleton<CaptureService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapHub<OscilloscopeHub>("/oscHub");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();


