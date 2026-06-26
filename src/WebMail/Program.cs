using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Services;
using WebMail.Services.Background;
using WebMail.Services.EmailProviders;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddDbContext<WebMailDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<BuyerRuleService>();
builder.Services.AddSingleton<CardGenerationService>();
builder.Services.AddSingleton<MailSyncPlanner>();
builder.Services.AddHttpClient<GmailProvider>();
builder.Services.AddScoped<IEmailProvider>(sp => sp.GetRequiredService<GmailProvider>());
builder.Services.AddHttpClient<OutlookProvider>();
builder.Services.AddScoped<IEmailProvider>(provider => provider.GetRequiredService<OutlookProvider>());
builder.Services.AddScoped<IEmailProviderResolver, EmailProviderResolver>();
builder.Services.AddSingleton<MailSyncJobQueueService>();
builder.Services.AddScoped<MailSyncProcessor>();
builder.Services.AddHostedService<MailSyncBackgroundService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => { options.LoginPath = "/Login"; options.AccessDeniedPath = "/AccessDenied"; });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Administrator"));
    options.AddPolicy("SalesOnly", policy => policy.RequireRole("Sales"));
    options.AddPolicy("SupplierOnly", policy => policy.RequireRole("Supplier"));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("Database is not auto-initialized (migrations deferred). DB-backed pages and the mail sync tick will fail until migrations/EnsureCreated are added.");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();
