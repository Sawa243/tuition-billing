using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using TuitionBilling.Api.Auth;
using TuitionBilling.Api.Middleware;
using TuitionBilling.Application;
using TuitionBilling.Infrastructure;
using TuitionBilling.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(o => o.SigningKey.Length >= 32,
        "Jwt:SigningKey должен быть не короче 32 символов и задаваться переменной окружения или dotnet user-secrets.")
    .ValidateOnStart();

builder.Services.AddSingleton<JwtTokenService>();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                string.IsNullOrWhiteSpace(jwt.SigningKey) ? new string('0', 32) : jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Статусы отдаются словами, а не числами: так читаемее и в Swagger, и в логах клиента.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Биллинг оплаты обучения",
        Version = "v1",
        Description = "Договоры, семестровые начисления, рассрочка, платежи через шлюз, возвраты, "
                      + "журнал двойной записи и суточная сверка."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Токен из POST /api/auth/login"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }]
            = Array.Empty<string>()
    });
});

var app = builder.Build();

await PrepareDatabaseAsync(app);

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSerilogRequestLogging();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Имена файлов интерфейса без хешей, поэтому браузеру нельзя разрешать
    // кэшировать их вслепую: после обновления он отдаст старую разметку
    // к новому API. no-cache означает «бери из кэша, но каждый раз переспроси».
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

app.UseSwagger();
app.UseSwaggerUI(options => options.DocumentTitle = "Биллинг оплаты обучения — API");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

app.Run();

static async Task PrepareDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    if (configuration.GetValue("Database:MigrateOnStartup", true))
    {
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        await db.Database.MigrateAsync();
    }

    if (configuration.GetValue("Seed:Enabled", false))
    {
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAsync(
            configuration["Seed:AccountantPassword"] ?? throw new InvalidOperationException("Не задан Seed:AccountantPassword."),
            configuration["Seed:StudentPassword"] ?? throw new InvalidOperationException("Не задан Seed:StudentPassword."),
            CancellationToken.None);
    }
}

// Метка сборки для интеграционных тестов: по ней WebApplicationFactory находит
// точку входа. Нужен именно отдельный класс, а не Program, — иначе он столкнётся
// с таким же Program эмулятора шлюза, который тесты поднимают рядом.
namespace TuitionBilling.Api
{
    public sealed class BillingApiMarker;
}
