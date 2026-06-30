using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebMail;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Services.Auth;
using WebMail.Services.Background;
using WebMail.Services.EmailProviders;
using WebMail.Services.Localization;
using WebMail.Services.Security;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddRazorPages()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(options =>
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
builder.Services.AddDbContext<WebMailDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<BuyerRuleService>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddScoped<CardKeyService>();
builder.Services.AddSingleton<CardGenerationService>();
builder.Services.AddSingleton<SnowflakeIdGenerator>();
builder.Services.AddScoped<ShipmentService>(sp =>
{
    var db = sp.GetRequiredService<WebMailDbContext>();
    var snowflake = sp.GetRequiredService<SnowflakeIdGenerator>();
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var cfg = sp.GetRequiredService<IConfiguration>();
    var rel = cfg["Shipments:StoragePath"] ?? "storage/shipments";
    var root = Path.IsPathRooted(rel) ? rel : Path.Combine(env.ContentRootPath, rel);
    return new ShipmentService(db, snowflake, root);
});
builder.Services.AddSingleton<MailSyncPlanner>();
builder.Services.AddHttpClient<GmailProvider>();
builder.Services.AddScoped<IEmailProvider>(sp => sp.GetRequiredService<GmailProvider>());
builder.Services.AddHttpClient<OutlookProvider>();
builder.Services.AddScoped<IEmailProvider>(provider => provider.GetRequiredService<OutlookProvider>());
builder.Services.AddScoped<IEmailProviderResolver, EmailProviderResolver>();
var dataProtection = builder.Services.AddDataProtection();
var dpKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dpKeysPath))
{
    // Persist keys outside the container so OAuth tokens / auth cookies survive rebuilds.
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath));
}
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Behind a reverse proxy on a private docker network the proxy IP is not fixed.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITokenProtector, DataProtectionTokenProtector>();
builder.Services.AddScoped<IOAuthStateStore, CookieOAuthStateStore>();
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
    options.AddPolicy("SupplierOrAdmin", policy => policy.RequireRole("Administrator", "Supplier"));
});

var app = builder.Build();

await using var scope = app.Services.CreateAsyncScope();
{
    var db = scope.ServiceProvider.GetRequiredService<WebMailDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "Shipments" (
          "Id" INTEGER NOT NULL CONSTRAINT "PK_Shipments" PRIMARY KEY AUTOINCREMENT,
          "BuyerId" INTEGER NOT NULL,
          "ShipmentNo" INTEGER NOT NULL,
          "StoredFileName" TEXT NOT NULL,
          "ContentType" TEXT NOT NULL,
          "Description" TEXT NOT NULL,
          "CreatedByUserId" INTEGER NULL,
          "CreatedAt" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_Shipments_BuyerId" ON "Shipments" ("BuyerId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Shipments_ShipmentNo" ON "Shipments" ("ShipmentNo");
        """);
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
    var seedUserName = builder.Configuration["Seed:AdminUserName"] ?? "admin";
    var seedPassword = builder.Configuration["Seed:AdminPassword"] ?? "Admin@123";
    await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, seedUserName, seedPassword);
}

// Configure the HTTP request pipeline.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRequestLocalization(LocalizationConfig.Build());

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();
