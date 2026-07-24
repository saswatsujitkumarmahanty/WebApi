using Microsoft.EntityFrameworkCore;
using Application.Interfaces;
using Infrastructure.Data;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAngularApp",
                policy =>
                {
                    policy.WithOrigins("http://localhost:4200") // Adjust if Angular runs on a different port
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                });
        });

        // Add services to the container.


        builder.Services.AddControllers();
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi();

        builder.Services.AddDbContext<RegistrationDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
        builder.Services.AddMemoryCache();

        builder.Services.AddScoped<IEmployeeRepository, EmployeeRepository>();
        builder.Services.AddScoped<IUserRepository, UserRepository>();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseCors("AllowAngularApp");

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapControllers();

        app.Run();
    }
}