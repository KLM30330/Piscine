using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;
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
    private readonly EzoPh _ph;
    private readonly Wk600Drive _drive;
    private readonly PumpPrimingService _priming;
    private readonly ILogger<MqttService> _logger;
    private IMqttClient? _client;

    // Protège contre les reconnexions simultanées
    private int _reconnecting = 0;

    private static readonly TimeSpan[] Backoffs =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
         TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];

    public MqttService(PoolConfig cfg, PoolState state,
        FiltrationManager filtration, PhPidController pid,
        EzoPh ph, Wk600Drive drive, PumpPrimingService priming, ILogger<MqttService> logger)
    {
        _cfg = cfg; _state = state;
        _filtration = filtration; _pid = pid;
        _ph = ph; _drive = drive; _priming = priming; _logger = logger;
        _priming.StateChanged += OnPrimingStateChanged;
    }

    // L'amorçage est asynchrone (chrono de plusieurs secondes) : on publie
    // l'état réel à chaque transition plutôt qu'en optimiste, pour que le
    // switch HA ne reste pas bloqué "ON" après la fin réelle de l'action.
    private void OnPrimingStateChanged(bool busy)
    {
        _ = Task.Run(async () =>
        {
            try { await PublishAsync($"{_cfg.MqttPrefix}/action/priming", busy ? "ON" : "OFF"); }
            catch (Exception ex) { _logger.LogError(ex, "Échec publication état amorçage"); }
        });
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
            if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
            try
            {
                _logger.LogWarning("MQTT déconnecté, reconnexion...");
                await ReconnectAsync(ct);
                // Republie la découverte HA : si le broker a perdu ses messages
                // retenus entre-temps (redémarrage sans persistance), les
                // entités ne réapparaîtraient sinon jamais sans relancer ce
                // service entier.
                try { await PublishHaDiscovery(ct); }
                catch (Exception ex) { _logger.LogError(ex, "Échec publication découverte HA (reconnexion)"); }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
        };

        await ReconnectAsync(ct);
        try { await PublishHaDiscovery(ct); }
        catch (Exception ex) { _logger.LogError(ex, "Échec publication découverte HA (initiale)"); }

        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_client!.IsConnected) return;

                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(_cfg.MqttBroker, _cfg.MqttPort)
                    .WithCredentials(_cfg.MqttUser, _cfg.MqttPassword)
                    .WithClientId(_cfg.MqttClientId)
                    .WithWillTopic($"{_cfg.MqttPrefix}/status")
                    .WithWillPayload("offline")
                    .WithWillRetain(true)
                    .Build();

                await _client.ConnectAsync(opts, ct);

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
                    if (double.TryParse(payload, System.Globalization.CultureInfo.InvariantCulture, out double hz))
                        _filtration.SetMode("forced", hz);
                    break;
                case "reset_ph":
                    _pid.Reset();
                    break;
                case "prime":
                    if (payload == "ON") _priming.TryPrime(_cfg.PrimeVolumeMl);
                    break;
                case "calibrate_mid":
                    if (payload == "ON")
                    {
                        await PublishAsync($"{_cfg.MqttPrefix}/action/calibrating", "ON", ct: ct);
                        _ph.CalibrateMid(_cfg.PhCalMidValue);
                        await PublishAsync($"{_cfg.MqttPrefix}/action/calibrating", "OFF", ct: ct);
                    }
                    break;
                case "reset_fault":
                    if (payload == "ON")
                    {
                        await PublishAsync($"{_cfg.MqttPrefix}/action/resetting_fault", "ON", ct: ct);
                        _drive.FaultReset();
                        await PublishAsync($"{_cfg.MqttPrefix}/action/resetting_fault", "OFF", ct: ct);
                    }
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

        async Task Sensor(string id, string name, string? stateKey,
                          string unit, string? devClass,
                          string stateTopic = "sensors", string? topicOverride = null)
        {
            // stateKey = null → topic en texte brut, pas de JSON (ex. pump_temp,
            // publié tel quel par PumpTempService) : pas de value_template,
            // HA utilise directement le payload comme état du capteur.
            string topic = topicOverride ?? $"{dev}/{stateTopic}";
            string? template = stateKey != null ? $"{{{{ value_json.{stateKey} }}}}" : null;

            var p = new HaDiscoveryPayload(
                name, $"{dev}_{id}", topic,
                null, template, unit, devClass, device);
            await PublishAsync(
                $"{_cfg.MqttHaDisc}/sensor/{dev}/{id}/config",
                JsonSerializer.Serialize(p, AppJsonContext.Default.HaDiscoveryPayload),
                retain: true, ct: ct);
        }

        async Task BinarySensor(string id, string name, string? stateKey,
                                 string? devClass,
                                 string stateTopic = "drive",
                                 string payloadOn  = "ON",
                                 string payloadOff = "OFF",
                                 string? topicOverride = null)
        {
            // value_template : Home Assistant parse le payload JSON puis évalue le
            // template Jinja2. Un booléen JSON true/false devient un booléen Python
            // True/False, et "{{ value_json.X }}" rend donc littéralement "True"/
            // "False" (majuscule) — qui ne correspondait à AUCUN des payload_on/off
            // ("true"/"false" en minuscule), d'où l'état "inconnue" affiché côté HA
            // pour tous les binary_sensor. On force ici un rendu textuel ON/OFF
            // non ambigu, identique au payload_on/payload_off attendu.
            // stateKey = null → pas de JSON, le topic transporte déjà "ON"/"OFF"
            // en texte brut (cas de l'électrolyseur, publié directement ainsi).
            string topic = topicOverride ?? $"{dev}/{stateTopic}";
            string? template = stateKey != null
                ? $"{{{{ 'ON' if value_json.{stateKey} else 'OFF' }}}}"
                : null;

            var p = new HaBinaryDiscoveryPayload(
                name, $"{dev}_{id}", topic,
                template, devClass,
                payloadOn, payloadOff, device);
            await PublishAsync(
                $"{_cfg.MqttHaDisc}/binary_sensor/{dev}/{id}/config",
                JsonSerializer.Serialize(p, AppJsonContext.Default.HaBinaryDiscoveryPayload),
                retain: true, ct: ct);
        }

        async Task Switch(string id, string name, string commandSuffix,
                           string payloadOn, string payloadOff,
                           string? stateKey = null, string? stateValue = null,
                           string stateTopic = "sensors", string? topicOverride = null)
        {
            // stateKey = état JSON (ex. FilterMode) ; topicOverride = état en
            // texte brut sur un topic dédié (ex. action/priming, déjà publié
            // en ON/OFF par MqttService/PumpPrimingService). Optimiste
            // uniquement si NI l'un NI l'autre n'est fourni — sinon le switch
            // resterait bloqué dans la position du dernier appui côté HA,
            // sans jamais refléter la fin réelle de l'action.
            string? topic = topicOverride ?? (stateKey != null ? $"{dev}/{stateTopic}" : null);
            string? template = stateKey != null
                ? $"{{{{ 'ON' if value_json.{stateKey} == '{stateValue}' else 'OFF' }}}}"
                : null;
            bool optimistic = topic == null;

            var p = new HaSwitchDiscoveryPayload(
                name, $"{dev}_{id}", $"{dev}/cmd/{commandSuffix}",
                topic, template, payloadOn, payloadOff,
                optimistic, device);
            await PublishAsync(
                $"{_cfg.MqttHaDisc}/switch/{dev}/{id}/config",
                JsonSerializer.Serialize(p, AppJsonContext.Default.HaSwitchDiscoveryPayload),
                retain: true, ct: ct);
        }

        async Task Number(string id, string name, string commandSuffix,
                           string stateKey, double min, double max, double step,
                           string unit, string stateTopic = "drive")
        {
            var p = new HaNumberDiscoveryPayload(
                name, $"{dev}_{id}", $"{dev}/cmd/{commandSuffix}",
                $"{dev}/{stateTopic}", $"{{{{ value_json.{stateKey} }}}}",
                min, max, step, unit, device);
            await PublishAsync(
                $"{_cfg.MqttHaDisc}/number/{dev}/{id}/config",
                JsonSerializer.Serialize(p, AppJsonContext.Default.HaNumberDiscoveryPayload),
                retain: true, ct: ct);
        }

        // ── Capteurs eau (topic: {prefix}/sensors) ────────────────────────────
        await Sensor("ph",         "pH Piscine",        "PhValue",    "pH",  null);
        await Sensor("orp",        "ORP Piscine",       "OrpMv",      "mV",  null);
        await Sensor("water_temp", "Température eau",   "WaterTempC", "°C",  "temperature");

        // ── Suivi dosage pH (topic: {prefix}/sensors) ──────────────────────────
        await Sensor("ph_dose_total", "Total acide injecté", "PhDoseTotalMl", "mL", "volume");

        // ── Capteurs variateur (topic: {prefix}/drive) ────────────────────────
        await Sensor("pump_freq",        "Fréquence pompe",       "OutFreqHz",   "Hz",  "frequency",   "drive");
        await Sensor("pump_current",     "Courant pompe",         "OutCurrentA", "A",   "current",     "drive");
        await Sensor("pump_voltage",     "Tension pompe",         "OutVoltageV", "V",   "voltage",     "drive");
        await Sensor("pump_power",       "Puissance pompe",       "OutPowerKw",  "W",  "power",       "drive");
        await Sensor("pump_setpoint",    "Consigne fréquence",    "SetpointHz",  "Hz",  "frequency",   "drive");
        await Sensor("pump_fault_code",  "Code défaut variateur", "FaultCode",   "",    null,          "drive");
        await Sensor("pump_fault_label", "Libellé défaut",        "FaultLabel",  "",    null,          "drive");

        // ── Température pompe (sonde DS18B20 one-wire, topic dédié
        // {prefix}/pump_temp déjà publié en texte brut par PumpTempService —
        // pas de JSON ici, contrairement aux capteurs ci-dessus) ──────────────
        await Sensor("pump_temp", "Température pompe", null, "°C", "temperature",
            topicOverride: $"{dev}/pump_temp");

        // ── Défaut sonde température pompe (lecture suspecte 0°C/85°C, ou
        // sonde absente/illisible) — topic dédié, texte brut ON/OFF ──────────
        await BinarySensor("pump_temp_fault", "Sonde température pompe en défaut",
            null, "problem", topicOverride: $"{dev}/pump_temp_fault");

        // ── Capteurs binaires variateur (topic: {prefix}/drive) ───────────────
        await BinarySensor("pump_running",     "Pompe en marche",  "IsRunning",  "running");
        await BinarySensor("pump_fault",       "Défaut variateur", "IsFault",    "problem");
        await BinarySensor("pump_at_setpoint", "À la consigne",    "AtSetpoint", null);

        // ── Alarmes pH/ORP (topic: {prefix}/sensors) ───────────────────────────
        await BinarySensor("ph_alarm_low",  "Alarme pH bas",  "PhAlarmLow", "problem", "sensors");
        await BinarySensor("orp_alarm",     "Alarme ORP",     "OrpAlarm",   "problem", "sensors");

        // ── Électrolyseur (topic dédié {prefix}/electrolyzer/state, déjà publié
        // en texte brut "ON"/"OFF" par ElectrolyzerService — pas de JSON ici) ───
        await BinarySensor("electrolyzer_running", "Electrolyseur en fonctionnement",
            null, "running", topicOverride: $"{dev}/electrolyzer/state");

        // ── Modes de filtration (topic commande: {prefix}/cmd/mode, partagé
        // entre les 4 switches ; état lu depuis SensorPayload.FilterMode) ──────
        await Switch("mode_auto",   "Mode filtration auto",    "mode", "auto",   "auto", "FilterMode", "Auto");
        await Switch("mode_forced", "Mode filtration forcée",  "mode", "forced", "auto", "FilterMode", "Forced");
        await Switch("mode_boost",  "Mode filtration boost",   "mode", "boost",  "auto", "FilterMode", "Boost");
        await Switch("mode_stop",   "Arrêt filtration",        "mode", "stop",   "auto", "FilterMode", "Stop");

        // ── Fréquence de fonctionnement de la pompe (Hz) — entité "number" et
        // non un switch : valeur continue, pas binaire. Réutilise cmd/freq,
        // qui bascule aussi en mode forcé (cohérent avec le bouton physique).
        await Number("pump_freq_setpoint", "Fréquence de fonctionnement pompe",
            "freq", "SetpointHz", min: _cfg.FreqMinAbsolute, max: _cfg.FreqNominal,
            step: 0.5, unit: "Hz");

        // ── Amorçage pompe péristaltique : état réel publié par
        // PumpPrimingService (busy pendant tout le chrono de dosage).
        await Switch("prime_pump", "Amorçage pompe péristaltique",
            "prime", "ON", "OFF", topicOverride: $"{dev}/action/priming");

        // ── Étalonnage pH (point milieu, solution tampon PhCalMidValue) ────────
        await Switch("calibrate_ph_mid", "Étalonnage pH milieu",
            "calibrate_mid", "ON", "OFF", topicOverride: $"{dev}/action/calibrating");

        // ── Réinitialisation défaut variateur : action ponctuelle. Vérifiez
        // que la cause du défaut a bien disparu (ex. moteur refroidi pour le
        // code 9) avant de réinitialiser, sinon le défaut reviendra aussitôt.
        await Switch("reset_fault_vfd", "Réinitialiser défaut variateur",
            "reset_fault", "ON", "OFF", topicOverride: $"{dev}/action/resetting_fault");

        // ── Santé des bus matériels (topic {prefix}/health, publié par
        // HealthService toutes les 30s) — un binary_sensor "problème" +
        // un sensor texte de diagnostic par bus.
        await BinarySensor("health_i2c",     "Problème bus I2C",         "I2cProblem",     "problem", "health");
        await BinarySensor("health_onewire", "Problème sonde one-wire",  "OneWireProblem", "problem", "health");
        await BinarySensor("health_rs485",   "Problème liaison RS485",   "Rs485Problem",   "problem", "health");
        await Sensor("health_i2c_error",     "Dernière erreur I2C",      "I2cLastError",     "", null, "health");
        await Sensor("health_onewire_error", "Dernière erreur one-wire", "OneWireLastError", "", null, "health");
        await Sensor("health_rs485_error",   "Dernière erreur RS485",    "Rs485LastError",   "", null, "health");
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
