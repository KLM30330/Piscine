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

    // Protège contre les reconnexions simultanées
    private int _reconnecting = 0;

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

        // DisconnectedAsync : relance la reconnexion, mais une seule à la fois
        _client.DisconnectedAsync += async args =>
        {
            if (ct.IsCancellationRequested) return;
            // Interlocked évite qu'une déconnexion en rafale lance plusieurs boucles
            if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
            try
            {
                _logger.LogWarning("MQTT déconnecté, reconnexion...");
                await ReconnectAsync(ct);
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
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
                // Si déjà connecté (ex: reconnexion rapide), on sort immédiatement
                if (_client!.IsConnected) return;

                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(_cfg.MqttBroker, _cfg.MqttPort)
                    .WithCredentials(_cfg.MqttUser, _cfg.MqttPassword)
                    .WithClientId(_cfg.MqttClientId)
                    .WithWillTopic($"{_cfg.MqttPrefix}/status")
                    .WithWillPayload("offline")
                    .WithWillRetain(true)
                    .Build();

                // ConnectAsync retourne seulement quand la connexion TCP+MQTT est établie
                await _client.ConnectAsync(opts, ct);

                // Subscribe après ConnectAsync — connexion garantie ici
                await _client.SubscribeAsync(
                    $"{_cfg.MqttPrefix}/cmd/#",
                    MqttQualityOfServiceLevel.AtMostOnce,
                    ct);

                _logger.LogInformation("MQTT connecté à {Broker}", _cfg.MqttBroker);
                await PublishAsync($"{_cfg.MqttPrefix}/status", "online", retain: true, ct: ct);
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

    public async Task PublishAsync(string topic, string payload,
        bool retain = false, CancellationToken ct = default)
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

        async Task Sensor(string id, string name, string stateKey,
                          string unit, string? devClass,
                          string stateTopic = "sensors")
        {
            var p = new HaDiscoveryPayload(
                name, $"{dev}_{id}", $"{dev}/{stateTopic}",
                null, $"{{{{ value_json.{stateKey} }}}}", unit, devClass, device);
            await PublishAsync(
                $"{_cfg.MqttHaDisc}/sensor/{dev}/{id}/config",
                JsonSerializer.Serialize(p, AppJsonContext.Default.HaDiscoveryPayload),
                retain: true, ct: ct);
        }

        async Task BinarySensor(string id, string name, string stateKey,
                                 string? devClass,
                                 string stateTopic = "drive",
                                 string payloadOn  = "true",
                                 string payloadOff = "false")
        {
            var p = new HaBinaryDiscoveryPayload(
                name, $"{dev}_{id}", $"{dev}/{stateTopic}",
                $"{{{{ value_json.{stateKey} }}}}", devClass,
                payloadOn, payloadOff, device);
            await PublishAsync(
                $"{_cfg.MqttHaDisc}/binary_sensor/{dev}/{id}/config",
                JsonSerializer.Serialize(p, AppJsonContext.Default.HaBinaryDiscoveryPayload),
                retain: true, ct: ct);
        }

        // ── Capteurs eau (topic: {prefix}/sensors) ────────────────────────────
        await Sensor("ph",         "pH Piscine",        "PhValue",    "pH",  null);
        await Sensor("orp",        "ORP Piscine",       "OrpMv",      "mV",  null);
        await Sensor("water_temp", "Température eau",   "WaterTempC", "°C",  "temperature");

        // ── Capteurs variateur (topic: {prefix}/drive) ────────────────────────
        await Sensor("pump_freq",        "Fréquence pompe",       "OutFreqHz",   "Hz",  "frequency",   "drive");
        await Sensor("pump_current",     "Courant pompe",         "OutCurrentA", "A",   "current",     "drive");
        await Sensor("pump_voltage",     "Tension pompe",         "OutVoltageV", "V",   "voltage",     "drive");
        await Sensor("pump_power",       "Puissance pompe",       "OutPowerKw",  "kW",  "power",       "drive");
        await Sensor("pump_temp",        "Température variateur", "DriveTempC",  "°C",  "temperature", "drive");
        await Sensor("pump_setpoint",    "Consigne fréquence",    "SetpointHz",  "Hz",  "frequency",   "drive");
        await Sensor("pump_fault_code",  "Code défaut variateur", "FaultCode",   "",    null,          "drive");
        await Sensor("pump_fault_label", "Libellé défaut",        "FaultLabel",  "",    null,          "drive");

        // ── Capteurs binaires variateur (topic: {prefix}/drive) ───────────────
        await BinarySensor("pump_running",     "Pompe en marche",  "IsRunning",  "running");
        await BinarySensor("pump_fault",       "Défaut variateur", "IsFault",    "problem");
        await BinarySensor("pump_at_setpoint", "À la consigne",    "AtSetpoint", null);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_client?.IsConnected == true)
        {
            await PublishAsync($"{_cfg.MqttPrefix}/status", "offline", retain: true, ct: ct);
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
        }
        await base.StopAsync(ct);
    }
}
