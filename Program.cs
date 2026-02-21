using FoundryWebUI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();

// Register LLM provider
builder.Services.AddHttpClient<FoundryLocalService>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromHours(2));
builder.Services.AddSingleton<ILlmProvider>(sp =>
    sp.GetRequiredService<FoundryLocalService>());

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
