using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Services.Auth;
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
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Login";
    options.AccessDeniedPath = "/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Administrator"));
    options.AddPolicy("SalesOnly", policy => policy.RequireRole("Sales"));
    options.AddPolicy("SupplierOnly", policy => policy.RequireRole("Supplier"));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WebMailDbContext>();
    await db.Database.EnsureCreatedAsync();
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
    var seedUserName = builder.Configuration["Seed:AdminUserName"] ?? "admin";
    var seedPassword = builder.Configuration["Seed:AdminPassword"] ?? "Admin@123";
    await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, seedUserName, seedPassword);
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
