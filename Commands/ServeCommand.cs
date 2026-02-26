using System.CommandLine;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PVAnalyze.Server;

namespace PVAnalyze.Commands;

public static class ServeCommand
{
    public static Command Create()
    {
        var portOption = new Option<int>("--port", () => 5001, "Port to listen on");
        var corsOption = new Option<bool>("--cors", () => true, "Enable CORS for local Electron app");

        var command = new Command("serve", "Start an HTTP server exposing trace analysis as REST API endpoints")
        {
            portOption,
            corsOption
        };

        command.SetHandler(Execute, portOption, corsOption);
        return command;
    }

    private static async Task Execute(int port, bool enableCors)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddSingleton<TraceSessionManager>();
        builder.Services.AddSingleton<CopilotService>(sp =>
            new CopilotService(sp.GetRequiredService<TraceSessionManager>()));

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        if (enableCors)
        {
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                });
            });
        }

        builder.WebHost.UseUrls($"http://localhost:{port}");

        var app = builder.Build();

        if (enableCors)
        {
            app.UseCors();
        }

        app.UseWebSockets();
        ApiEndpoints.Map(app);

        Console.Error.WriteLine($"pvanalyze server listening on http://localhost:{port}");
        Console.Error.WriteLine("Press Ctrl+C to stop.");

        await app.RunAsync();
    }
}
