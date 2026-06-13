using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Ph;
using System.Text;
using System.Text.Json;

namespace PiscineController.Services;

public sealed class MqttService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly FiltrationManager _filtration;
    private readonly PhPidController _pid;
    private readonly ILogger<MqttService> _logger;
    private IMqttClient? _client;
    private static readonly TimeSpan[] Backoffs =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
         TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];

    public MqttService(PoolConfig cfg, PoolState state,
        FiltrationManager filtration, PhPidController pid,
        ILogger<MqttService> logger)
    {
        _cfg = cfg; _state = state;
        _filtration = filtration; _pid = pid; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += async args =>
        {
            if (ct.IsCancellationRequested) return;
            _logger.LogWarning("MQTT déconnecté, reconnexion...");
            await ReconnectAsync(ct);
        };

        await ReconnectAsync(ct);
        await PublishHaDiscovery(ct);

        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(_cfg.MqttBroker, _cfg.MqttPort)
                    .WithCredentials(_cfg.MqttUser, _cfg.MqttPassword)
                    .WithClientId(_cfg.MqttClientId)
                    .WithWillTopic($"{_cfg.MqttPrefix}/status")
                    .WithWillPayload("offline")
                    .WithWillRetain(true)
                    .Build();
                await _client!.ConnectAsync(opts, ct);

                await _client.SubscribeAsync($"{_cfg.MqttPrefix}/cmd/#", MqttQualityOfServiceLevel.AtMostOnce, ct);
                _logger.LogInformation("MQTT connecté à {Broker}", _cfg.MqttBroker);
                await PublishAsync($"{_cfg.MqttPrefix}/status", "online", retain: true, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var delay = Backoffs[Math.Min(attempt++, Backoffs.Length - 1)];
                _logger.LogWarning(ex, "MQTT connexion échouée, retry dans {D}s", delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken ct = default)
    {
        if (_client?.IsConnected != true) return;
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic).WithPayload(payload).WithRetainFlag(retain).Build();
            await _client.PublishAsync(msg, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "MQTT publish échoué ({Topic})", topic); }
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        string topic = args.ApplicationMessage.Topic;
        var seq = args.ApplicationMessage.Payload;
        string payload = seq.IsSingleSegment
            ? Encoding.UTF8.GetString(seq.FirstSpan)
            : Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(in seq));
        _logger.LogDebug("MQTT cmd: {Topic} = {Payload}", topic, payload);

        string cmd = topic.Replace($"{_cfg.MqttPrefix}/cmd/", "");
        try
        {
            switch (cmd)
            {
                case "mode":
                    _filtration.SetMode(payload);
                    _state.FilterMode = Enum.Parse<FilterMode>(char.ToUpper(payload[0]) + payload[1..]);
                    break;
                case "freq":
                    if (double.TryParse(payload, out double hz))
                        _filtration.SetMode("forced", hz);
                    break;
                case "reset_ph":
                    _pid.Reset();
                    break;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Erreur traitement commande {Cmd}", cmd); }
        return Task.CompletedTask;
    }

    private async Task PublishHaDiscovery(CancellationToken ct)
    {
        string dev = _cfg.MqttPrefix;
        var device = new HaDeviceInfo(
            [$"{_cfg.MqttPrefix}_controller"],
            "Piscine", "RPi3B+ / Atlas Scientific / WK600-D", "DIY");

        async Task Sensor(string id, string name, string stateKey, string unit, string? devClass)
        {
            var p = new HaDiscoveryPayload(
                name, $"{dev}_{id}", $"{dev}/sensors",
                null, $"{{{{ value_json.{stateKey} }}}}", unit, devClass, device);
            await PublishAsync(
                $"{_cfg.MqttHaDisc}/sensor/{dev}/{id}/config",
                JsonSerializer.Serialize(p, AppJsonContext.Default.HaDiscoveryPayload),
                retain: true, ct);
        }

        await Sensor("ph", "pH Piscine", "PhValue", "pH", null);
        await Sensor("orp", "ORP Piscine", "OrpMv", "mV", null);
        await Sensor("water_temp", "Température eau", "WaterTempC", "°C", "temperature");
        await Sensor("pump_freq", "Fréquence pompe", "TargetFreqHz", "Hz", "frequency");
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_client?.IsConnected == true)
        {
            await PublishAsync($"{_cfg.MqttPrefix}/status", "offline", retain: true, ct);
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
        }
        await base.StopAsync(ct);
    }
}
