using System.Text.Json.Serialization;
using PiscineController.Config;

namespace PiscineController.Config;

[JsonSerializable(typeof(SensorPayload))]
[JsonSerializable(typeof(DriveStatusPayload))]
[JsonSerializable(typeof(HaDiscoveryPayload))]
[JsonSerializable(typeof(HaBinaryDiscoveryPayload))]
[JsonSerializable(typeof(HaDeviceInfo))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AppJsonContext : JsonSerializerContext { }

public sealed record SensorPayload(
    double PhValue, double OrpMv, double WaterTempC,
    string WaterState, double TargetFreqHz, bool OrpAlarm,
    double PhDoseTotalMl, bool PhAlarmLow);

public sealed record DriveStatusPayload(
    double OutFreqHz, double OutCurrentA, double OutVoltageV,
    double OutPowerKw, int RunTimeH,
    int FaultCode, string FaultLabel, bool IsRunning, bool IsFault,
    bool AtSetpoint, double SetpointHz);

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
