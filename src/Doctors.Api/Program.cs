using Doctors.Api.Endpoints;
using Doctors.Api.Extensions;
using Doctors.Application;
using Doctors.Infrastructure;
using Doctors.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NSwag.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// OpenAPI with NSwag
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument();

var app = builder.Build();

// Auto-apply migrations on startup (for dev/demo)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DoctorDbContext>();
    await db.Database.MigrateAsync();
}

// Static files (for ReDoc HTML page)
app.UseStaticFiles();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseOpenApi();
    app.UseSwaggerUi();
}

// Global exception handler
app.UseProblemDetails();

// Map endpoints
app.MapDoctorEndpoints();

app.Run();
