using System.Text.Json.Serialization;
using PiscineController.Config;

namespace PiscineController.Config;

[JsonSerializable(typeof(PoolConfig))]
[JsonSerializable(typeof(SensorPayload))]
[JsonSerializable(typeof(DriveStatusPayload))]
[JsonSerializable(typeof(HaDiscoveryPayload))]
[JsonSerializable(typeof(HaDeviceInfo))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AppJsonContext : JsonSerializerContext { }

public sealed record SensorPayload(
    double PhValue, double OrpMv, double WaterTempC,
    string WaterState, double TargetFreqHz, bool OrpAlarm);

public sealed record DriveStatusPayload(
    double OutFreqHz, double OutCurrentA, double OutVoltageV,
    double OutPowerKw, double DriveTempC, int RunTimeH,
    int FaultCode, string FaultLabel, bool IsRunning, bool IsFault,
    bool AtSetpoint, double SetpointHz);

public sealed record HaDeviceInfo(
    string[] Identifiers, string Name, string Model, string Manufacturer);

public sealed record HaDiscoveryPayload(
    string Name, string UniqueId, string StateTopic,
    string? CommandTopic, string? ValueTemplate, string? UnitOfMeasurement,
    string? DeviceClass, HaDeviceInfo Device);
