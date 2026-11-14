using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Server;

/// <summary>Builds the RhinoTrunk web application. Used by Program.cs and by the in-process API tests.</summary>
public static class ServerApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

        var megabytes = builder.Configuration.GetValue("Limits:MaxRequestBodyMegabytes", 512);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = megabytes * 1024L * 1024L);

        builder.Services.AddSingleton<IRepositoryProvider>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var kind = config["Storage:Provider"] ?? "FileSystem";
            if (kind.Equals("InMemory", StringComparison.OrdinalIgnoreCase)) return new InMemoryRepositoryProvider();
            var root = config["Storage:Root"] ?? "data";
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("RhinoTrunk").LogInformation("File system storage at {Root}", Path.GetFullPath(root));
            return new FileSystemRepositoryProvider(root);
        });
        builder.Services.AddSingleton(sp => new RhinoTrunkService(sp.GetRequiredService<IRepositoryProvider>()));

        var app = builder.Build();
        app.Use(HandleErrorsAsync);
        Endpoints.Map(app);
        return app;
    }

    /// <summary>Maps every failure to the <see cref="ApiError"/> JSON shape.</summary>
    private static async Task HandleErrorsAsync(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (RhinoTrunkException e)
        {
            await WriteErrorAsync(context, e.Status, e.ToApiError());
        }
        catch (BadHttpRequestException e)
        {
            var message = e.InnerException is JsonException je ? "Malformed JSON: " + je.Message : e.Message;
            await WriteErrorAsync(context, e.StatusCode, new ApiError { Code = ErrorCodes.InvalidRequest, Message = message });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RhinoTrunk").LogError(e, "Unhandled error");
            await WriteErrorAsync(context, 500, new ApiError { Code = ErrorCodes.InternalError, Message = "Unexpected server error; see the server log." });
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, ApiError error)
    {
        if (context.Response.HasStarted) throw new InvalidOperationException("Response already started: " + error.Message);
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(error, RhinoTrunkJson.Options);
    }
}
