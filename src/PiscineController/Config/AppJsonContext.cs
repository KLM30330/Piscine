using System.Text.Json.Serialization;
using PiscineController.Config;

namespace PiscineController.Config;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SensorPayload))]
[JsonSerializable(typeof(DriveStatusPayload))]
[JsonSerializable(typeof(HealthPayload))]
[JsonSerializable(typeof(SchedulePayload))]
[JsonSerializable(typeof(List<ScheduleSlot>))]
[JsonSerializable(typeof(HaDiscoveryPayload))]
[JsonSerializable(typeof(HaBinaryDiscoveryPayload))]
[JsonSerializable(typeof(HaSwitchDiscoveryPayload))]
[JsonSerializable(typeof(HaNumberDiscoveryPayload))]
[JsonSerializable(typeof(HaDeviceInfo))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AppJsonContext : JsonSerializerContext { }

public sealed record SensorPayload(
    double PhValue, double OrpMv, double WaterTempC,
    string WaterState, double TargetFreqHz, bool OrpAlarm,
    double PhDoseTotalMl, bool PhAlarmLow,
    string FilterMode, double PumpForcedFreqHz);

public sealed record DriveStatusPayload(
    double OutFreqHz, double OutCurrentA, double OutVoltageV,
    double OutPowerKw, int RunTimeH,
    int FaultCode, string FaultLabel, bool IsRunning, bool IsFault,
    bool AtSetpoint, double SetpointHz);

// Champs "Problem" (et non "Ok") pour s'aligner directement sur le
// device_class="problem" de Home Assistant : true = ON = problème détecté.
public sealed record HealthPayload(
    bool I2cProblem, bool Rs485Problem,
    string I2cLastError, string Rs485LastError);

// Planning de filtration journalier publié sur pool/schedule après chaque
// rebuild. StartH/EndH = heures décimales (ex. 8.5 = 08h30). Label = texte
// lisible "08h30–10h00" pour l'affichage direct dans HA ou le panneau tactile.
public sealed record ScheduleSlot(
    [property: JsonPropertyName("start_h")]  double StartH,
    [property: JsonPropertyName("end_h")]    double EndH,
    [property: JsonPropertyName("label")]    string Label);

public sealed record SchedulePayload(
    [property: JsonPropertyName("required_hours")]  double RequiredHours,
    [property: JsonPropertyName("water_temp_c")]    double WaterTempC,
    [property: JsonPropertyName("slots")]           List<ScheduleSlot> Slots,
    [property: JsonPropertyName("slots_label")]     string SlotsLabel);

public sealed record HaDeviceInfo(
    [property: JsonPropertyName("identifiers")]  string[] Identifiers,
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("model")]        string Model,
    [property: JsonPropertyName("manufacturer")] string Manufacturer);

public sealed record HaDiscoveryPayload(
    [property: JsonPropertyName("name")]                string Name,
    [property: JsonPropertyName("unique_id")]           string UniqueId,
    [property: JsonPropertyName("state_topic")]         string StateTopic,
    [property: JsonPropertyName("command_topic")]       string? CommandTopic,
    [property: JsonPropertyName("value_template")]      string? ValueTemplate,
    [property: JsonPropertyName("unit_of_measurement")] string? UnitOfMeasurement,
    [property: JsonPropertyName("device_class")]        string? DeviceClass,
    [property: JsonPropertyName("device")]              HaDeviceInfo Device);

public sealed record HaBinaryDiscoveryPayload(
    [property: JsonPropertyName("name")]           string Name,
    [property: JsonPropertyName("unique_id")]      string UniqueId,
    [property: JsonPropertyName("state_topic")]    string StateTopic,
    [property: JsonPropertyName("value_template")] string? ValueTemplate,
    [property: JsonPropertyName("device_class")]   string? DeviceClass,
    [property: JsonPropertyName("payload_on")]     string PayloadOn,
    [property: JsonPropertyName("payload_off")]    string PayloadOff,
    [property: JsonPropertyName("device")]         HaDeviceInfo Device);

public sealed record HaSwitchDiscoveryPayload(
    [property: JsonPropertyName("name")]           string Name,
    [property: JsonPropertyName("unique_id")]      string UniqueId,
    [property: JsonPropertyName("command_topic")]  string CommandTopic,
    [property: JsonPropertyName("state_topic")]    string? StateTopic,
    [property: JsonPropertyName("value_template")] string? ValueTemplate,
    [property: JsonPropertyName("payload_on")]     string PayloadOn,
    [property: JsonPropertyName("payload_off")]    string PayloadOff,
    [property: JsonPropertyName("state_on")]       string? StateOn,
    [property: JsonPropertyName("state_off")]      string? StateOff,
    [property: JsonPropertyName("optimistic")]     bool? Optimistic,
    [property: JsonPropertyName("device")]         HaDeviceInfo Device);

public sealed record HaNumberDiscoveryPayload(
    [property: JsonPropertyName("name")]                 string Name,
    [property: JsonPropertyName("unique_id")]            string UniqueId,
    [property: JsonPropertyName("command_topic")]        string CommandTopic,
    [property: JsonPropertyName("state_topic")]          string? StateTopic,
    [property: JsonPropertyName("value_template")]       string? ValueTemplate,
    [property: JsonPropertyName("min")]                  double Min,
    [property: JsonPropertyName("max")]                  double Max,
    [property: JsonPropertyName("step")]                 double Step,
    [property: JsonPropertyName("unit_of_measurement")]  string? UnitOfMeasurement,
    [property: JsonPropertyName("device")]               HaDeviceInfo Device);
