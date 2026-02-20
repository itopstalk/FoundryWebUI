using FoundryWebUI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();

// Register LLM providers
builder.Services.AddHttpClient<FoundryLocalService>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<OllamaService>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton<ILlmProvider>(sp =>
    sp.GetRequiredService<FoundryLocalService>());
builder.Services.AddSingleton<ILlmProvider>(sp =>
    sp.GetRequiredService<OllamaService>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();

app.Run();
