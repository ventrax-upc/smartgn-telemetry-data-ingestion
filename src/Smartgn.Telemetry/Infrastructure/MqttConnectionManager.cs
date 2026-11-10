using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using Smartgn.Telemetry.Configuration;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Infrastructure;

/// <summary>
/// Singleton connection to the MQTT 5 broker (report component
/// "MqttConnectionManager"): one client per process, QoS 1 subscription to
/// <c>smartgn/devices/+/telemetry</c> and reconnects with the documented
/// 1-2-4-8-16-30 s backoff. Inbound messages are only acknowledged after the
/// consumer handled them, so a crash before storage triggers redelivery and
/// deduplication absorbs the duplicate.
/// </summary>
public sealed class MqttConnectionManager : IDisposable
{
    private static readonly TimeSpan[] ReconnectBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    ];

    private readonly MqttSettings _settings;
    private readonly ILogger<MqttConnectionManager> _logger;
    private readonly IMqttClient _client;
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _runner;

    public MqttConnectionManager(MqttSettings settings, ILogger<MqttConnectionManager> logger)
    {
        _settings = settings;
        _logger = logger;

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <summary>Raised for every acknowledged inbound application message.</summary>
    public event Func<string, byte[], Task>? MessageReceived;

    public bool IsConnected => _client.IsConnected;

    /// <summary>Starts the connect/subscribe/supervise loop in the background.</summary>
    public void Start()
    {
        if (_runner is not null)
        {
            return;
        }

        _runner = Task.Run(() => RunAsync(_stopping.Token));
    }

    public async Task StopAsync()
    {
        _stopping.Cancel();
        if (_runner is not null)
        {
            try
            {
                await _runner;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "MQTT disconnect raised during shutdown");
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _client.ConnectAsync(BuildOptions(), cancellationToken);
                await SubscribeAsync(cancellationToken);
                _logger.LogInformation("Connected to MQTT broker {Url} as {ClientId}", _settings.Url, _settings.ClientId);
                attempt = 0;

                if (_client.IsConnected)
                {
                    await _disconnected.Task.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "MQTT connection cycle failed");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var delay = ReconnectBackoff[Math.Min(attempt, ReconnectBackoff.Length - 1)];
            attempt++;
            _logger.LogInformation("Reconnecting to MQTT broker in {Delay}s", delay.TotalSeconds);
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        var options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(filter => filter.WithTopic(_settings.Topic).WithAtLeastOnceQoS())
            .Build();
        await _client.SubscribeAsync(options, cancellationToken);
    }

    private MqttClientOptions BuildOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(_settings.ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanSession();

        var uri = new Uri(_settings.Url);
        var useTls = uri.Scheme is "ssl" or "tls" or "mqtts";
        var port = uri.Port > 0 ? uri.Port : (useTls ? 8883 : 1883);
        builder = builder.WithTcpServer(uri.Host, port);

        if (!string.IsNullOrEmpty(_settings.Username))
        {
            builder = builder.WithCredentials(_settings.Username, _settings.Password ?? string.Empty);
        }

        if (useTls)
        {
            builder = builder.WithTlsOptions(new MqttClientTlsOptions
            {
                UseTls = true,
                AllowUntrustedCertificates = true,
                IgnoreCertificateChainErrors = true,
            });
        }

        return builder.Build();
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            var handler = MessageReceived;
            if (handler is not null)
            {
                await handler.Invoke(
                    args.ApplicationMessage.Topic,
                    args.ApplicationMessage.PayloadSegment.ToArray());
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Telemetry consumer failed for topic {Topic}", args.ApplicationMessage.Topic);
        }
        finally
        {
            try
            {
                await args.AcknowledgeAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "MQTT acknowledgment failed (redelivery will be deduplicated)");
            }
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        _logger.LogWarning("MQTT connection lost: {Reason}", args.ClientWasConnected ? args.Reason.ToString() : "not_connected");
        _disconnected.TrySetResult();
        return Task.CompletedTask;
    }
}
