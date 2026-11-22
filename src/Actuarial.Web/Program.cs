using Actuarial.Web.Components;
using Actuarial.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Shell>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton(sp => new AppState((IJSInProcessRuntime)sp.GetRequiredService<IJSRuntime>()));

await builder.Build().RunAsync();
