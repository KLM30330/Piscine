# Piscine Controller — .NET 10 AOT Rewrite Design

**Date:** 2026-06-12  
**Status:** Approved

## Context

Rewrite the Python pool controller daemon (`pool_controller.py` + modules) into a .NET 10 NativeAOT binary targeting a Raspberry Pi 3B+ (`linux-arm`). The system controls pool hardware via I2C, 1-Wire, GPIO, and Modbus RTU, publishing telemetry to Home Assistant via MQTT.

## Constraints

- **Deployment:** Binary + systemd service on RPi 3B+ (no Docker)
- **Target RID:** `linux-arm` (ARMv7, Raspberry Pi 3B+)
- **Config:** `appsettings.json` with source-gen binder (no reflection)
- **Tests:** Unit tests for pure logic only — `PhPidController` + `FiltrationManager`
- **CI publish:** GitHub Release asset (binary) on tag push
- **AOT:** `PublishAot=true`, zero runtime reflection

## Project Structure

```
Piscine/
  src/
    PiscineController/
      PiscineController.csproj
      Program.cs
      PoolState.cs
      Config/
        PoolConfig.cs
        AppJsonContext.cs
      Hardware/
        AtlasI2c.cs
        Pcf8574.cs
        Lcd1602.cs
        Ds18b20.cs
        Wk600Drive.cs
        GpioButtons.cs
      Services/
        MqttService.cs
        SensorService.cs
        DriveService.cs
        PumpTempService.cs
        ElectrolyzerService.cs
        ButtonService.cs
        DisplayService.cs
      Filtration/
        FiltrationManager.cs
        FilterMode.cs
      Ph/
        PhPidController.cs
  tests/
    PiscineController.Tests/
      PiscineController.Tests.csproj
      Ph/
        PhPidControllerTests.cs
      Filtration/
        FiltrationManagerTests.cs
  Directory.Build.props
  PiscineController.sln
  .github/
    dependabot.yml
    workflows/
      ci.yml
      publish.yml
      security.yml
      dependabot-auto-merge.yml
  systemd/
    piscine-controller.service
```

## Architecture

**Approach:** Fine-grained `BackgroundService` per hardware domain + `PoolState` singleton.

`Program.cs` uses `Host.CreateDefaultBuilder` to register all services via DI. Each `BackgroundService.ExecuteAsync` runs its own loop with `await Task.Delay(interval, ct)`. The host's `CancellationToken` propagates shutdown on SIGTERM/SIGINT.

### PoolState (shared singleton)

Thread-safe shared state between services:

```csharp
public sealed class PoolState
{
    public double WaterTempC;    // Interlocked via double cast
    public double PhValue;
    public double OrpMv;
    public FilterMode Mode;      // Interlocked.Exchange on int cast
    public bool PumpRunning;
    public double PumpFreqHz;
    // DriveStatus struct → lock(_lock) for composite reads/writes
}
```

### Services

| Service | Interval | Responsibility |
|---------|----------|----------------|
| `MqttService` | event-driven | MQTTnet v5, pub/sub, reconnect, LWT, HA autodiscovery |
| `SensorService` | 60s | Atlas EZO poll, pH PID, ORP → filtration, publish |
| `DriveService` | 30s | WK600-D Modbus telemetry, publish |
| `PumpTempService` | 30s | DS18B20 1-Wire read, publish |
| `ElectrolyzerService` | 5s | PCF8574 relay on/off based on PoolState |
| `ButtonService` | event-driven | GPIO interrupts → actions on FiltrationManager + DisplayService |
| `DisplayService` | command-driven | LCD1602 via Channel<DisplayCommand> (serialized access) |

**MQTT commands** are dispatched in `MqttService.OnCommandReceived` directly to `FiltrationManager`, `PhPidController`, `Pcf8574` — no extra indirection layer.

## AOT Critical Points

### JSON Source Generation

All MQTT payloads serialized via explicit context:

```csharp
[JsonSerializable(typeof(HaEntityPayload))]
[JsonSerializable(typeof(HaDeviceInfo))]
[JsonSerializable(typeof(DriveStatusPayload))]
internal partial class AppJsonContext : JsonSerializerContext { }
```

Always: `JsonSerializer.Serialize(obj, AppJsonContext.Default.XxxType)`. Never naked `JsonSerializer.Serialize(obj)`.

### Config Binding

```csharp
services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>()
      .GetSection("Pool")
      .Get<PoolConfig>(ConfigBinderContext.Default)!);
```

`PoolConfig` is a plain POCO covering all constants from `config.py`.

### Hardware Libraries

| Component | Library | AOT Status |
|-----------|---------|-----------|
| Atlas EZO (I2C) | `System.Device.I2c` + custom protocol | ✓ AOT-safe |
| PCF8574 relay | `System.Device.I2c` + custom | ✓ AOT-safe |
| LCD1602 | `System.Device.I2c` + custom | ✓ AOT-safe |
| DS18B20 | `File.ReadAllText("/sys/bus/w1/...")` | ✓ AOT-safe |
| WK600-D Modbus | `FluentModbus` | ⚠ validate trim warnings |
| GPIO buttons | `System.Device.Gpio` | ✓ AOT-safe |

FluentModbus mitigation if trim warnings appear: `rd.xml` with `<Assembly Name="FluentModbus" Dynamic="Required All"/>`.

### csproj

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <OutputType>Exe</OutputType>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <InvariantGlobalization>true</InvariantGlobalization>
  <PublishAot>true</PublishAot>
  <RuntimeIdentifier>linux-arm</RuntimeIdentifier>
</PropertyGroup>
```

## Error Handling & Shutdown

**Transient hardware errors:** caught in each service loop, logged, wait for next interval. No process crash.

**Critical startup failure** (Atlas sensors unreachable): `IHostApplicationLifetime.StopApplication()`.

**MQTT reconnect:** exponential backoff (1s → 2s → 4s → max 30s). `PublishAsync` swallows exceptions when disconnected (log warning). LWT guarantees `pool/status = offline` on hard kill.

**Graceful shutdown order** (reverse DI registration):
1. `MqttService` → publish `offline` + disconnect
2. `ButtonService` → unregister GPIO callbacks
3. `ElectrolyzerService` → relay OFF
4. `SensorService` / `DriveService` / `PumpTempService` → exit loops
5. Hardware `DisposeAsync`: ramp-stop WK600, close I2C bus, close serial

**systemd unit:**
```ini
[Service]
Restart=on-failure
RestartSec=5s
KillSignal=SIGTERM
TimeoutStopSec=15s
```

## Testing

Project: `PiscineController.Tests` — `net10.0`, xUnit, no AOT (xUnit incompatible with NativeAOT).

### PhPidControllerTests
- Dose = 0 when pH within deadband
- Dose clamped to `dose_max`
- Dose blocked when `min_delay_s` not elapsed
- `total_ml` accumulates correctly
- Reset clears state

### FiltrationManagerTests
- Filtration hours = T°C / 2 (clamped to pool volume / pump flow)
- Time slots correctly cover required hours
- Mode transitions: auto → pause → resume_auto
- ORP below threshold → increase pump frequency
- ORP above threshold → decrease pump frequency
- Boost mode exits when ORP target reached

No hardware mocks, no I2C/GPIO/Modbus dependencies in tested classes.

## CI/CD Workflows

### `ci.yml` (push + PR)
```
dotnet restore → dotnet build → dotnet test
```
No AOT publish (too slow for every commit).

### `publish.yml` (tag `X.Y.Z.W`)
1. Validate tag == `<Version>` in `Directory.Build.props`
2. `apt-get install clang gcc-arm-linux-gnueabihf zlib1g-dev`
3. `dotnet publish -c Release -r linux-arm --self-contained /p:PublishAot=true`
4. Upload binary as GitHub Release asset

### `security.yml` (Monday 08:00)
- `dotnet list package --vulnerable --include-transitive`
- Trivy scan on `mcr.microsoft.com/dotnet/runtime-deps:10.0-bookworm-slim-arm32v7`

### `dependabot.yml`
- NuGet: weekly, Monday, all-nuget group
- GitHub Actions: weekly, Monday, all-actions group

### `dependabot-auto-merge.yml`
- Auto-merge patch/minor Dependabot PRs
- Bump `<Version>` patch in `Directory.Build.props`
- Auto-tag and push

## Gains vs Python

| | Python | .NET 10 AOT |
|---|---|---|
| Startup | ~2s | ~50ms |
| RAM | ~45 MB | ~8 MB |
| Deployment | pip + venv | single binary |
| Type safety | partial (hints) | full |
| Async | `threading.Thread` | `async/await` |
| CI | none | build + publish + security + dependabot |
