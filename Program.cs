using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using NAudio.Codecs;
using Twilio.AspNet.Core;
using Twilio.TwiML;
using Twilio.TwiML.Voice;
using Vosk;
using Task = System.Threading.Tasks.Task;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(
    options => options.ForwardedHeaders = ForwardedHeaders.All
);

var app = builder.Build();

var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

// You can set to -1 to disable logging messages
Vosk.Vosk.SetLogLevel(0);
Model model = new Model("model");
SpkModel spkModel = new SpkModel("model-spk");
VoskRecognizer rec = new VoskRecognizer(model, 16000.0f);
rec.SetSpkModel(spkModel);

app.UseForwardedHeaders();

app.UseWebSockets();

app.MapPost("/voice", (HttpRequest request) =>
{
    var response = new VoiceResponse();
    var connect = new Connect();
    connect.Stream(url: $"wss://{request.Host}/stream");
    response.Append(connect);
    return Results.Extensions.TwiML(response);
});

app.MapGet("/stream", async (HttpContext context) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await Echo(webSocket);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

async Task Echo(WebSocket webSocket)
{
    var buffer = new byte[1024 * 4];
    var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

    while (!receiveResult.CloseStatus.HasValue &&
           !appLifetime.ApplicationStopping.IsCancellationRequested)
    {
        using var jsonDocument = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, receiveResult.Count));
        var eventMessage = jsonDocument.RootElement.GetProperty("event").GetString();

        switch (eventMessage)
        {
            case "connected":
                Console.WriteLine("Event: connected");
                break;
            case "start":
                Console.WriteLine("Event: start");
                var streamSid = jsonDocument.RootElement.GetProperty("streamSid").GetString();
                Console.WriteLine($"StreamId: {streamSid}");
                break;
            case "media":
                var payload = jsonDocument.RootElement.GetProperty("media").GetProperty("payload").GetString();
                byte[] data = Convert.FromBase64String(payload);
                short[] dataShort = new short[data.Length];

                // TODO: ulaw 8000 bitrate to Linear 16000 bitrate
                for (int i = 0; i < data.Length; i++)
                {
                    dataShort[i] = MuLawDecoder.MuLawToLinearSample(data[i]);
                }

                if (rec.AcceptWaveform(dataShort, dataShort.Length))
                {
                    var json = JsonSerializer.Deserialize<JsonDocument>(rec.Result());
                    Console.Write(json.RootElement.GetProperty("text").GetString());
                }
                else
                {
                    var json = JsonSerializer.Deserialize<JsonDocument>(rec.PartialResult());
                    Console.Write(json.RootElement.GetProperty("partial").GetString());
                }
                break;
            case "stop":
                Console.WriteLine("Event: stop");
                break;
        }

        receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    }

    if (receiveResult.CloseStatus.HasValue)
    {
        await webSocket.CloseAsync(
            receiveResult.CloseStatus.Value,
            receiveResult.CloseStatusDescription,
            CancellationToken.None);
    }
    else if (appLifetime.ApplicationStopping.IsCancellationRequested)
    {
        await webSocket.CloseAsync(
            WebSocketCloseStatus.EndpointUnavailable,
            "Server shutting down",
            CancellationToken.None);
    }
}

app.Run();
