using Eling.Application;
using Eling.Core;
using Eling.Index;
using Eling.Storage;
using Eling.Server.Converters;
using Eling.Server.Dtos;
using Eling.Server.Endpoints;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var rootPath = builder.Configuration["Eling:RootPath"] ?? ".eling";

builder.Services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(rootPath));
builder.Services.AddSingleton<IMemoryIndex>(new SqliteMemoryIndex(Path.Combine(rootPath, "index.db")));
builder.Services.AddSingleton<IMemoryService, MemoryService>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new MemoryIdJsonConverter());
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
}

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseCors();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapMemoryRoutes();

app.Run();

public partial class Program;