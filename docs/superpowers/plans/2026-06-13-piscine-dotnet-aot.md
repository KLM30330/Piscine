# Piscine Controller — .NET 10 AOT Rewrite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rewrite Python pool controller daemon into a .NET 10 NativeAOT binary targeting Raspberry Pi 3B+ (`linux-arm`), controlling pool hardware via I2C, 1-Wire, GPIO, and Modbus RTU, publishing telemetry to Home Assistant via MQTT.

**Architecture:** Fine-grained `BackgroundService` per hardware domain + `PoolState` singleton. `Program.cs` wires all services via DI. Each service runs its own `await Task.Delay(interval, ct)` loop. `CancellationToken` from host propagates SIGTERM/SIGINT shutdown.

**Tech Stack:** .NET 10 NativeAOT, MQTTnet v5, System.Device.Gpio, System.Device.I2c, System.IO.Ports, System.Text.Json source-gen, xUnit (tests only, no AOT), GitHub Actions

---

## Structure des fichiers

```
Piscine/
  src/PiscineController/
    PiscineController.csproj
    Program.cs
    PoolState.cs
    Config/
      PoolConfig.cs
      AppJsonContext.cs
    Hardware/
      AtlasI2c.cs          ← base class EZO + EzoPh, EzoOrp, EzoRtd, EzoPmp
      Pcf8574.cs           ← relais électrolyseur
      Lcd1602.cs           ← afficheur LCD
      Ds18b20.cs           ← sonde température 1-Wire
      Wk600Drive.cs        ← variateur Modbus RTU (client minimal, pas FluentModbus)
      GpioButtons.cs       ← 4 boutons GPIO avec interruptions
    Services/
      MqttService.cs       ← MQTTnet v5, reconnexion, LWT, autodiscovery HA
      SensorService.cs     ← boucle 60s Atlas + pH PID + ORP → filtration
      DriveService.cs      ← boucle 30s télémétrie WK600-D
      PumpTempService.cs   ← boucle 30s DS18B20
      ElectrolyzerService.cs ← boucle 5s relais électrolyseur
      ButtonService.cs     ← interruptions GPIO → actions
      DisplayService.cs    ← Channel<DisplayCommand> → LCD sérialisé
    Filtration/
      FiltrationManager.cs ← logique pure (sans drive), TDD
      FilterMode.cs
    Ph/
      PhPidController.cs   ← logique pure PID, TDD
  tests/PiscineController.Tests/
    PiscineController.Tests.csproj
    Ph/PhPidControllerTests.cs
    Filtration/FiltrationManagerTests.cs
  Directory.Build.props
  PiscineController.sln
  .github/
    dependabot.yml
    workflows/
      ci.yml
      publish.yml
      security.yml
      dependabot-auto-merge.yml
  systemd/piscine-controller.service
```

---

### Tâche 1 : Scaffold solution (.sln, .csproj, Directory.Build.props)

**Fichiers :**
- Créer : `PiscineController.sln`
- Créer : `Directory.Build.props`
- Créer : `src/PiscineController/PiscineController.csproj`
- Créer : `tests/PiscineController.Tests/PiscineController.Tests.csproj`

- [ ] **Étape 1 : Créer l'arborescence**

```bash
mkdir -p src/PiscineController tests/PiscineController.Tests
dotnet new sln -n PiscineController
dotnet new console -n PiscineController -o src/PiscineController --framework net10.0
dotnet new xunit -n PiscineController.Tests -o tests/PiscineController.Tests --framework net10.0
dotnet sln add src/PiscineController/PiscineController.csproj
dotnet sln add tests/PiscineController.Tests/PiscineController.Tests.csproj
```

- [ ] **Étape 2 : Écrire `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Version>1.0.0.0</Version>
  </PropertyGroup>
</Project>
```

- [ ] **Étape 3 : Écrire `src/PiscineController/PiscineController.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <InvariantGlobalization>true</InvariantGlobalization>
    <PublishAot>true</PublishAot>
    <!-- RuntimeIdentifier passé à dotnet publish, pas ici → tests tournent sur Windows x64 -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.*" />
    <PackageReference Include="MQTTnet" Version="5.*" />
    <PackageReference Include="System.Device.Gpio" Version="3.*" />
  </ItemGroup>
</Project>
```

- [ ] **Étape 4 : Écrire `tests/PiscineController.Tests/PiscineController.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\PiscineController\PiscineController.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Étape 5 : Vérifier le build**

```bash
dotnet build
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 6 : Commit**

```bash
git add Directory.Build.props PiscineController.sln src/ tests/
git commit -m "chore: scaffold solution — sln, csproj, Directory.Build.props"
```

---

### Tâche 2 : PoolConfig + appsettings.json + AppJsonContext + PoolState + DriveStatus

**Fichiers :**
- Créer : `src/PiscineController/Config/PoolConfig.cs`
- Créer : `src/PiscineController/Config/AppJsonContext.cs`
- Créer : `src/PiscineController/PoolState.cs`
- Créer : `src/PiscineController/appsettings.json`

- [ ] **Étape 1 : Écrire `Config/PoolConfig.cs`**

```csharp
namespace PiscineController.Config;

public sealed class PoolConfig
{
    // MQTT
    public string MqttBroker { get; init; } = "192.168.1.XX";
    public int MqttPort { get; init; } = 1883;
    public string MqttUser { get; init; } = "pool_user";
    public string MqttPassword { get; init; } = "";
    public string MqttClientId { get; init; } = "pool_controller";
    public string MqttPrefix { get; init; } = "pool";
    public string MqttHaDisc { get; init; } = "homeassistant";

    // Hardware I2C
    public int I2cBus { get; init; } = 1;
    public int AtlasPhAddr { get; init; } = 0x63;
    public int AtlasOrpAddr { get; init; } = 0x62;
    public int AtlasRtdAddr { get; init; } = 0x66;
    public int AtlasPmpAddr { get; init; } = 0x67;
    public int Pcf8574Addr { get; init; } = 0x20;
    public int LcdI2cAddr { get; init; } = 0x27;

    // 1-Wire DS18B20
    public string? OnewirePumpSensorId { get; init; } = null;
    public double PumpTempAlertC { get; init; } = 60.0;
    public double PumpTempCriticalC { get; init; } = 70.0;

    // GPIO boutons (BCM)
    public int BtnLcdDisplay { get; init; } = 5;
    public int BtnPrimePump { get; init; } = 6;
    public int BtnPauseFilter { get; init; } = 13;
    public int BtnResumeFilter { get; init; } = 19;

    // Modbus WK600-D
    public string ModbusPort { get; init; } = "/dev/ttyUSB0";
    public int ModbusSlaveId { get; init; } = 1;
    public int ModbusBaudrate { get; init; } = 9600;
    public string ModbusParity { get; init; } = "N";
    public int ModbusStopbits { get; init; } = 1;

    // Fréquences moteur
    public double FreqMinAbsolute { get; init; } = 30.0;
    public double FreqMinFiltration { get; init; } = 35.0;
    public double FreqStartMin { get; init; } = 40.0;
    public double FreqNominal { get; init; } = 50.0;
    public double FreqRampStep { get; init; } = 2.0;
    public double FreqRampDelay { get; init; } = 0.5;

    // Hydraulique
    public double PoolVolumeM3 { get; init; } = 38.0;
    public double PumpFlowM3H { get; init; } = 17.0;
    public int[][] FiltrationSlots { get; init; } = [[8, 13], [14, 21]];

    // ORP
    public double OrpCriticalLow { get; init; } = 550.0;
    public double OrpLow { get; init; } = 620.0;
    public double OrpTargetLow { get; init; } = 650.0;
    public double OrpTargetHigh { get; init; } = 750.0;
    public double OrpHigh { get; init; } = 800.0;
    public int OrpStabilityN { get; init; } = 3;
    public double OrpMinFreqChange { get; init; } = 5.0;
    public double OrpMinChangeInterval { get; init; } = 120.0;

    // pH PID
    public double PhTarget { get; init; } = 7.2;
    public double PhDeadband { get; init; } = 0.05;
    public double PhKp { get; init; } = 10.0;
    public double PhKi { get; init; } = 0.1;
    public double PhKd { get; init; } = 1.0;
    public double PhDoseMinMl { get; init; } = 1.0;
    public double PhDoseMaxMl { get; init; } = 50.0;
    public double PhMinDelayS { get; init; } = 600.0;
    public double PrimeVolumeMl { get; init; } = 20.0;

    // Boost
    public double BoostFreq { get; init; } = 50.0;
    public double BoostOrpTarget { get; init; } = 700.0;

    // LCD
    public int LcdDisplayDuration { get; init; } = 30;
    public int LcdBacklightTimeout { get; init; } = 60;
}
```

- [ ] **Étape 2 : Écrire `appsettings.json`**

```json
{
  "Pool": {
    "MqttBroker": "192.168.1.XX",
    "MqttPort": 1883,
    "MqttUser": "pool_user",
    "MqttPassword": "VotreMotDePasse",
    "OnewirePumpSensorId": null,
    "FiltrationSlots": [[8, 13], [14, 21]]
  }
}
```

- [ ] **Étape 3 : Écrire `Config/AppJsonContext.cs`**

```csharp
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
```

- [ ] **Étape 4 : Écrire `PoolState.cs`**

```csharp
using PiscineController.Filtration;

namespace PiscineController;

public sealed class PoolState
{
    // Scalaires → lecture/écriture via Volatile (double non atomique sur ARM 32-bit)
    private double _waterTempC;
    private double _phValue;
    private double _orpMv;
    private double _pumpFreqHz;
    private int _filterMode; // cast de FilterMode
    private int _pumpRunning; // 0/1

    public double WaterTempC
    {
        get => Volatile.Read(ref _waterTempC);
        set => Volatile.Write(ref _waterTempC, value);
    }
    public double PhValue
    {
        get => Volatile.Read(ref _phValue);
        set => Volatile.Write(ref _phValue, value);
    }
    public double OrpMv
    {
        get => Volatile.Read(ref _orpMv);
        set => Volatile.Write(ref _orpMv, value);
    }
    public double PumpFreqHz
    {
        get => Volatile.Read(ref _pumpFreqHz);
        set => Volatile.Write(ref _pumpFreqHz, value);
    }
    public FilterMode FilterMode
    {
        get => (FilterMode)Volatile.Read(ref _filterMode);
        set => Volatile.Write(ref _filterMode, (int)value);
    }
    public bool PumpRunning
    {
        get => Volatile.Read(ref _pumpRunning) == 1;
        set => Volatile.Write(ref _pumpRunning, value ? 1 : 0);
    }

    // DriveStatus composite → lock pour lecture/écriture atomique
    private readonly object _driveLock = new();
    private DriveStatusPayload? _driveStatus;
    public DriveStatusPayload? DriveStatus
    {
        get { lock (_driveLock) return _driveStatus; }
        set { lock (_driveLock) _driveStatus = value; }
    }
}
```

- [ ] **Étape 5 : Build**

```bash
dotnet build
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 6 : Commit**

```bash
git add src/PiscineController/Config/ src/PiscineController/PoolState.cs src/PiscineController/appsettings.json
git commit -m "feat: PoolConfig, AppJsonContext, PoolState"
```

---

### Tâche 3 : FilterMode + WaterState + FiltrationManager (TDD)

**Fichiers :**
- Créer : `src/PiscineController/Filtration/FilterMode.cs`
- Créer : `src/PiscineController/Filtration/FiltrationManager.cs`
- Créer : `tests/PiscineController.Tests/Filtration/FiltrationManagerTests.cs`

> `FiltrationManager` est **pur** : pas de dépendance drive. Le service layer (Tâche 11) orchestre le drive.

- [ ] **Étape 1 : Écrire `Filtration/FilterMode.cs`**

```csharp
namespace PiscineController.Filtration;

public enum FilterMode { Auto, Forced, Boost, Pause, Stop }

public enum WaterState
{
    Unknown, CriticalLow, Low, BorderLow, Optimal, BorderHigh, Overdose
}
```

- [ ] **Étape 2 : Écrire les tests (rouge)**

`tests/PiscineController.Tests/Filtration/FiltrationManagerTests.cs` :

```csharp
using PiscineController.Config;
using PiscineController.Filtration;

namespace PiscineController.Tests.Filtration;

public class FiltrationManagerTests
{
    private static PoolConfig Cfg() => new();

    // required_hours : règle T/2, minimum pool/pompe
    [Theory]
    [InlineData(8.0, 2.24)]   // < 10°C → 2h brut mais min = 38/17 ≈ 2.24
    [InlineData(10.0, 3.0)]   // < 12°C → 3h
    [InlineData(20.0, 10.0)]  // 20/2 = 10h
    [InlineData(30.0, 24.0)]  // > 28°C → 24h
    public void RequiredHours_FollowsRule(double temp, double expectedH)
    {
        var mgr = new FiltrationManager(Cfg());
        Assert.Equal(expectedH, mgr.RequiredHours(temp), 1);
    }

    // ORP → WaterState
    [Theory]
    [InlineData(500.0, WaterState.CriticalLow)]
    [InlineData(580.0, WaterState.Low)]
    [InlineData(630.0, WaterState.BorderLow)]
    [InlineData(700.0, WaterState.Optimal)]
    [InlineData(760.0, WaterState.BorderHigh)]
    [InlineData(820.0, WaterState.Overdose)]
    public void ClassifyOrp_CorrectState(double orp, WaterState expected)
    {
        var mgr = new FiltrationManager(Cfg());
        Assert.Equal(expected, mgr.ClassifyOrp(orp));
    }

    // Stabilité ORP : 3 lectures consécutives pour confirmer
    [Fact]
    public void UpdateOrp_ConfirmsAfter3ConsistentReadings()
    {
        var mgr = new FiltrationManager(Cfg());
        mgr.UpdateOrp(700.0); // 1
        Assert.Equal(WaterState.Unknown, mgr.WaterState);
        mgr.UpdateOrp(700.0); // 2
        Assert.Equal(WaterState.Unknown, mgr.WaterState);
        mgr.UpdateOrp(700.0); // 3 → confirmé
        Assert.Equal(WaterState.Optimal, mgr.WaterState);
    }

    // Fréquence cible selon état ORP
    [Theory]
    [InlineData(500.0, 50.0)]  // CriticalLow → 50 Hz
    [InlineData(630.0, 45.0)]  // BorderLow → 45 Hz
    [InlineData(820.0, 30.0)]  // Overdose → 30 Hz
    public void TargetFreq_MatchesOrpState(double orp, double expectedHz)
    {
        var mgr = new FiltrationManager(Cfg());
        double freq = mgr.OrpTargetFreq(mgr.ClassifyOrp(orp), orp);
        Assert.Equal(expectedHz, freq, 0);
    }

    // Mode AUTO → PAUSE → resume_auto
    [Fact]
    public void ModeTransition_AutoPauseResume()
    {
        var mgr = new FiltrationManager(Cfg());
        Assert.Equal(FilterMode.Auto, mgr.Mode);
        mgr.SetMode("pause");
        Assert.Equal(FilterMode.Pause, mgr.Mode);
        mgr.ResumeAuto();
        Assert.Equal(FilterMode.Auto, mgr.Mode);
    }

    // ShouldPumpRun selon mode
    [Theory]
    [InlineData("forced", true)]
    [InlineData("boost",  true)]
    [InlineData("pause",  false)]
    [InlineData("stop",   false)]
    public void ShouldPumpRun_ByMode(string mode, bool expected)
    {
        var mgr = new FiltrationManager(Cfg());
        mgr.SetMode(mode);
        Assert.Equal(expected, mgr.ShouldPumpRun());
    }

    // Boost exit quand ORP atteint cible
    [Fact]
    public void BoostExit_WhenOrpReachesTarget()
    {
        var mgr = new FiltrationManager(Cfg());
        mgr.SetMode("boost");
        mgr.CheckBoostExit(699.0); // < 700 → reste boost
        Assert.Equal(FilterMode.Boost, mgr.Mode);
        mgr.CheckBoostExit(700.0); // = target → auto
        Assert.Equal(FilterMode.Auto, mgr.Mode);
    }

    // build_schedule distribue les heures dans les créneaux
    [Fact]
    public void BuildSchedule_DistributesAcrossSlots()
    {
        var mgr = new FiltrationManager(Cfg());
        var slots = mgr.BuildSchedule(20.0, 50.0); // 10h requis
        double total = slots.Sum(s => s.End - s.Start);
        Assert.Equal(10.0, total, 1);
    }
}
```

- [ ] **Étape 3 : Lancer les tests (rouge attendu)**

```bash
dotnet test tests/PiscineController.Tests/ --filter "Filtration"
```
Résultat attendu : erreur de compilation `FiltrationManager not found`.

- [ ] **Étape 4 : Écrire `Filtration/FiltrationManager.cs`**

```csharp
using PiscineController.Config;

namespace PiscineController.Filtration;

public sealed record FiltrationSlot(double Start, double End);

public sealed class FiltrationManager
{
    private readonly PoolConfig _cfg;
    private FilterMode _mode = FilterMode.Auto;
    private WaterState _waterState = WaterState.Unknown;
    private readonly List<WaterState> _orpHistory = [];
    private double _targetFreqHz;
    private double _forcedFreqHz;
    private List<FiltrationSlot> _schedule = [];
    private int _scheduleDay = -1;

    public FiltrationManager(PoolConfig cfg)
    {
        _cfg = cfg;
        _targetFreqHz = cfg.FreqNominal;
        _forcedFreqHz = cfg.FreqNominal;
    }

    public FilterMode Mode => _mode;
    public WaterState WaterState => _waterState;
    public double TargetFreqHz => _targetFreqHz;

    public double RequiredHours(double tempC)
    {
        double minH = _cfg.PoolVolumeM3 / _cfg.PumpFlowM3H;
        double raw = tempC switch
        {
            < 10 => 2.0,
            < 12 => 3.0,
            < 16 => 4.0 + (tempC - 12) * 0.5,
            <= 28 => tempC / 2.0,
            _ => 24.0
        };
        return Math.Max(raw, minH);
    }

    public double CorrectedHours(double tempC, double freqHz)
    {
        double freq = Math.Max(freqHz, _cfg.FreqMinAbsolute);
        return Math.Min(RequiredHours(tempC) * (_cfg.FreqNominal / freq), 24.0);
    }

    public WaterState ClassifyOrp(double orp) => orp switch
    {
        < _ when orp < _cfg.OrpCriticalLow => WaterState.CriticalLow,
        < _ when orp < _cfg.OrpLow         => WaterState.Low,
        < _ when orp < _cfg.OrpTargetLow   => WaterState.BorderLow,
        < _ when orp <= _cfg.OrpTargetHigh => WaterState.Optimal,
        < _ when orp <= _cfg.OrpHigh       => WaterState.BorderHigh,
        _ => WaterState.Overdose
    };

    public double OrpTargetFreq(WaterState state, double orp)
    {
        if (state == WaterState.Optimal)
        {
            double ratio = Math.Clamp(
                (orp - _cfg.OrpTargetLow) / (_cfg.OrpTargetHigh - _cfg.OrpTargetLow), 0, 1);
            return Math.Round(_cfg.FreqMinFiltration - ratio * (_cfg.FreqMinFiltration - _cfg.FreqMinAbsolute), 1);
        }
        return state switch
        {
            WaterState.CriticalLow or WaterState.Low or WaterState.Unknown => _cfg.FreqNominal,
            WaterState.BorderLow => 45.0,
            WaterState.BorderHigh or WaterState.Overdose => _cfg.FreqMinAbsolute,
            _ => _cfg.FreqNominal
        };
    }

    public (WaterState State, double TargetHz, bool Alarm) UpdateOrp(double orp)
    {
        var newState = ClassifyOrp(orp);
        _orpHistory.Add(newState);
        if (_orpHistory.Count > _cfg.OrpStabilityN)
            _orpHistory.RemoveAt(0);

        bool confirmed = _orpHistory.Count >= _cfg.OrpStabilityN
                      && _orpHistory.All(s => s == newState);
        if (confirmed && newState != _waterState)
            _waterState = newState;

        double freq = OrpTargetFreq(newState, orp);
        bool alarm = newState is WaterState.CriticalLow or WaterState.Overdose;
        return (newState, freq, alarm);
    }

    public void SetTargetFreq(double hz) =>
        _targetFreqHz = Math.Max(hz, _cfg.FreqMinAbsolute);

    public void SetMode(string modeStr, double? freqHz = null)
    {
        _mode = modeStr.ToLowerInvariant() switch
        {
            "auto"   => FilterMode.Auto,
            "forced" => FilterMode.Forced,
            "boost"  => FilterMode.Boost,
            "pause"  => FilterMode.Pause,
            "stop"   => FilterMode.Stop,
            _ => _mode
        };
        if (freqHz.HasValue)
            _forcedFreqHz = Math.Max(freqHz.Value, _cfg.FreqMinAbsolute);
    }

    public void ResumeAuto() => _mode = FilterMode.Auto;

    public bool ShouldPumpRun() => _mode switch
    {
        FilterMode.Auto   => InSchedule(),
        FilterMode.Forced => true,
        FilterMode.Boost  => true,
        FilterMode.Pause  => false,
        FilterMode.Stop   => false,
        _ => false
    };

    public double GetRunFreq() => _mode switch
    {
        FilterMode.Forced => _forcedFreqHz,
        FilterMode.Boost  => _cfg.BoostFreq,
        _ => _targetFreqHz
    };

    public void CheckBoostExit(double orp)
    {
        if (_mode == FilterMode.Boost && orp >= _cfg.BoostOrpTarget)
            _mode = FilterMode.Auto;
    }

    public List<FiltrationSlot> BuildSchedule(double tempC, double freqHz = 50.0)
    {
        double total = CorrectedHours(tempC, freqHz);
        if (total >= 23.5)
        {
            _schedule = [new(0.0, 24.0)];
        }
        else
        {
            double remaining = total;
            var result = new List<FiltrationSlot>();
            foreach (var slot in _cfg.FiltrationSlots)
            {
                if (remaining <= 0) break;
                double slotLen = slot[1] - slot[0];
                double alloc = Math.Min(remaining, slotLen);
                result.Add(new(slot[0], slot[0] + alloc));
                remaining -= alloc;
            }
            if (remaining > 0)
                result.Add(new(22.0, Math.Min(22.0 + remaining, 30.0)));
            _schedule = result;
        }
        _scheduleDay = DateTime.Now.DayOfYear;
        return _schedule;
    }

    public bool NeedsRebuild() =>
        _schedule.Count == 0 || DateTime.Now.DayOfYear != _scheduleDay;

    private bool InSchedule()
    {
        double cur = DateTime.Now.Hour + DateTime.Now.Minute / 60.0;
        foreach (var s in _schedule)
        {
            if (s.End > 24.0)
            { if (cur >= s.Start || cur < s.End - 24.0) return true; }
            else if (cur >= s.Start && cur < s.End) return true;
        }
        return false;
    }
}
```

- [ ] **Étape 5 : Lancer les tests (vert attendu)**

```bash
dotnet test tests/PiscineController.Tests/ --filter "Filtration"
```
Résultat attendu : tous les tests `PASS`.

- [ ] **Étape 6 : Commit**

```bash
git add src/PiscineController/Filtration/ tests/PiscineController.Tests/Filtration/
git commit -m "feat: FiltrationManager + FilterMode + WaterState (TDD)"
```

---

### Tâche 4 : PhPidController (TDD)

**Fichiers :**
- Créer : `src/PiscineController/Ph/PhPidController.cs`
- Créer : `tests/PiscineController.Tests/Ph/PhPidControllerTests.cs`

- [ ] **Étape 1 : Écrire les tests (rouge)**

```csharp
using PiscineController.Config;
using PiscineController.Ph;

namespace PiscineController.Tests.Ph;

public class PhPidControllerTests
{
    private static PoolConfig Cfg() => new();

    [Fact]
    public void Dose_ZeroWhenWithinDeadband()
    {
        var pid = new PhPidController(Cfg());
        double dose = pid.Compute(7.2, pumpRunning: true); // exactement sur cible
        Assert.Equal(0.0, dose);
    }

    [Fact]
    public void Dose_ZeroWhenPumpNotRunning()
    {
        var pid = new PhPidController(Cfg());
        double dose = pid.Compute(7.5, pumpRunning: false); // pH trop haut mais pompe OFF
        Assert.Equal(0.0, dose);
    }

    [Fact]
    public void Dose_ClampedToMax()
    {
        var pid = new PhPidController(Cfg());
        // Premier appel → pas de délai
        double dose = pid.Compute(8.5, pumpRunning: true); // déviation forte
        Assert.True(dose <= Cfg().PhDoseMaxMl);
    }

    [Fact]
    public void Dose_BlockedByMinDelay()
    {
        var pid = new PhPidController(Cfg());
        pid.Compute(7.5, pumpRunning: true); // première dose
        double dose2 = pid.Compute(7.5, pumpRunning: true); // trop tôt
        Assert.Equal(0.0, dose2);
    }

    [Fact]
    public void TotalMl_Accumulates()
    {
        var pid = new PhPidController(Cfg());
        double d1 = pid.Compute(7.5, pumpRunning: true);
        Assert.Equal(d1, pid.TotalMl);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var pid = new PhPidController(Cfg());
        pid.Compute(7.5, pumpRunning: true);
        pid.Reset();
        Assert.Equal(0.0, pid.TotalMl);
    }

    [Fact]
    public void Dose_ZeroWhenPhBelowTarget()
    {
        var pid = new PhPidController(Cfg());
        // pH trop bas (6.8) → pas de dosage acide (on dose seulement si pH > cible)
        double dose = pid.Compute(6.8, pumpRunning: true);
        Assert.Equal(0.0, dose);
    }
}
```

- [ ] **Étape 2 : Lancer les tests (rouge attendu)**

```bash
dotnet test tests/PiscineController.Tests/ --filter "Ph"
```
Résultat attendu : erreur de compilation `PhPidController not found`.

- [ ] **Étape 3 : Écrire `Ph/PhPidController.cs`**

```csharp
using PiscineController.Config;

namespace PiscineController.Ph;

public sealed class PhPidController
{
    private readonly PoolConfig _cfg;
    private double _integral;
    private double _prevError;
    private double _lastDoseTime = double.MinValue;
    private double _totalMl;

    public PhPidController(PoolConfig cfg) => _cfg = cfg;

    public double TotalMl => _totalMl;

    public double Compute(double phValue, bool pumpRunning)
    {
        if (!pumpRunning) return 0.0;

        double error = phValue - _cfg.PhTarget;

        // Deadband
        if (Math.Abs(error) <= _cfg.PhDeadband) return 0.0;

        // On dose seulement si pH > cible (réduction pH par acide)
        if (error <= 0) return 0.0;

        // Délai minimum entre doses
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - _lastDoseTime < _cfg.PhMinDelayS) return 0.0;

        _integral += error;
        double derivative = error - _prevError;
        _prevError = error;

        double dose = _cfg.PhKp * error
                    + _cfg.PhKi * _integral
                    + _cfg.PhKd * derivative;

        dose = Math.Clamp(dose, _cfg.PhDoseMinMl, _cfg.PhDoseMaxMl);
        _lastDoseTime = now;
        _totalMl += dose;
        return dose;
    }

    public void Reset()
    {
        _integral = 0;
        _prevError = 0;
        _lastDoseTime = double.MinValue;
        _totalMl = 0;
    }
}
```

- [ ] **Étape 4 : Lancer les tests (vert attendu)**

```bash
dotnet test tests/PiscineController.Tests/ --filter "Ph"
```
Résultat attendu : tous les tests `PASS`.

- [ ] **Étape 5 : Lancer tous les tests**

```bash
dotnet test
```
Résultat attendu : `Build succeeded`, tous tests `PASS`.

- [ ] **Étape 6 : Commit**

```bash
git add src/PiscineController/Ph/ tests/PiscineController.Tests/Ph/
git commit -m "feat: PhPidController (TDD)"
```

---

### Tâche 5 : AtlasI2c — base class + EzoPh, EzoOrp, EzoRtd, EzoPmp

**Fichiers :**
- Créer : `src/PiscineController/Hardware/AtlasI2c.cs`

- [ ] **Étape 1 : Ajouter `System.Device.Gpio` au csproj (déjà présent), vérifier**

```bash
dotnet list src/PiscineController/ package | grep Device
```
Résultat attendu : `System.Device.Gpio`.

- [ ] **Étape 2 : Écrire `Hardware/AtlasI2c.cs`**

```csharp
using System.Device.I2c;
using Microsoft.Extensions.Logging;

namespace PiscineController.Hardware;

/// <summary>
/// Protocole Atlas Scientific EZO sur I2C.
/// Séquence : écrire commande → attendre processing_delay → lire réponse.
/// Byte[0] = code réponse (1=succès, 2=syntaxe, 254=pending, 255=aucune donnée).
/// </summary>
public abstract class AtlasEzoBase : IDisposable
{
    private readonly I2cDevice _device;
    protected readonly ILogger _logger;
    private bool _disposed;

    protected AtlasEzoBase(int busId, int address, ILogger logger)
    {
        _logger = logger;
        _device = I2cDevice.Create(new I2cConnectionSettings(busId, address));
    }

    protected string? SendCommand(string command, int delayMs = 900)
    {
        Span<byte> tx = stackalloc byte[command.Length];
        for (int i = 0; i < command.Length; i++) tx[i] = (byte)command[i];
        _device.Write(tx);

        Thread.Sleep(delayMs);

        Span<byte> rx = stackalloc byte[32];
        _device.Read(rx);

        if (rx[0] != 1)
        {
            _logger.LogWarning("Atlas EZO réponse code {Code} pour commande '{Cmd}'", rx[0], command);
            return null;
        }
        int len = rx[1..].IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(rx[1..(len < 0 ? 32 : len + 1)]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.Dispose();
    }
}

public sealed class EzoPh : AtlasEzoBase
{
    public EzoPh(int busId, int address, ILogger<EzoPh> logger)
        : base(busId, address, logger) { }

    public double? Read()
    {
        var raw = SendCommand("R", 900);
        return raw != null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    public void CalibrateMid(double ph) => SendCommand($"Cal,mid,{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1400);
    public void CalibrateLow(double ph) => SendCommand($"Cal,low,{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1400);
    public void CalibrateHigh(double ph) => SendCommand($"Cal,high,{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1400);
}

public sealed class EzoOrp : AtlasEzoBase
{
    public EzoOrp(int busId, int address, ILogger<EzoOrp> logger)
        : base(busId, address, logger) { }

    public double? Read()
    {
        var raw = SendCommand("R", 900);
        return raw != null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}

public sealed class EzoRtd : AtlasEzoBase
{
    public EzoRtd(int busId, int address, ILogger<EzoRtd> logger)
        : base(busId, address, logger) { }

    public double? Read()
    {
        var raw = SendCommand("R", 600);
        return raw != null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}

public sealed class EzoPmp : AtlasEzoBase
{
    public EzoPmp(int busId, int address, ILogger<EzoPmp> logger)
        : base(busId, address, logger) { }

    /// <summary>Dose en mL. Bloquant ~(vol/20)*1000 ms.</summary>
    public void Dose(double volumeMl)
    {
        int delayMs = (int)(volumeMl / 20.0 * 1000) + 2000;
        SendCommand($"D,{volumeMl.ToString(System.Globalization.CultureInfo.InvariantCulture)}", delayMs);
    }

    public void Stop() => SendCommand("X", 300);
}
```

- [ ] **Étape 3 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 4 : Commit**

```bash
git add src/PiscineController/Hardware/AtlasI2c.cs
git commit -m "feat: Atlas EZO I2C base class + EzoPh, EzoOrp, EzoRtd, EzoPmp"
```

---

### Tâche 6 : Pcf8574 + Lcd1602

**Fichiers :**
- Créer : `src/PiscineController/Hardware/Pcf8574.cs`
- Créer : `src/PiscineController/Hardware/Lcd1602.cs`

- [ ] **Étape 1 : Écrire `Hardware/Pcf8574.cs`**

```csharp
using System.Device.I2c;

namespace PiscineController.Hardware;

/// <summary>
/// PCF8574 — expandeur I2C 8 bits.
/// Logique active haut : bit=1 → relais ON.
/// Lecture/écriture du registre complet (1 octet).
/// </summary>
public sealed class Pcf8574 : IDisposable
{
    private readonly I2cDevice _device;
    private byte _state;

    public Pcf8574(int busId, int address)
    {
        _device = I2cDevice.Create(new I2cConnectionSettings(busId, address));
        _state = 0x00;
        Write();
    }

    public void SetPin(int pin, bool value)
    {
        if (value) _state |=  (byte)(1 << pin);
        else       _state &= (byte)~(1 << pin);
        Write();
    }

    public bool GetPin(int pin) => (_state & (1 << pin)) != 0;

    private void Write() => _device.WriteByte(_state);

    public void Dispose() => _device.Dispose();
}
```

- [ ] **Étape 2 : Écrire `Hardware/Lcd1602.cs`**

```csharp
using System.Device.I2c;

namespace PiscineController.Hardware;

/// <summary>
/// LCD1602 via backpack I2C (PCF8574 interne, adresse 0x27).
/// Mode 4 bits. Backlight contrôlé par bit3 du backpack.
/// </summary>
public sealed class Lcd1602 : IDisposable
{
    private readonly I2cDevice _device;
    private bool _backlight = true;

    private const byte LCD_BACKLIGHT = 0x08;
    private const byte ENABLE = 0x04;
    private const byte RS_DATA = 0x01;

    public Lcd1602(int busId, int address)
    {
        _device = I2cDevice.Create(new I2cConnectionSettings(busId, address));
        Thread.Sleep(50);
        Init();
    }

    private void Init()
    {
        // Séquence initialisation 4 bits
        WriteFourBits(0x03); Thread.Sleep(5);
        WriteFourBits(0x03); Thread.Sleep(1);
        WriteFourBits(0x03); Thread.Sleep(1);
        WriteFourBits(0x02);
        SendCommand(0x28); // 4-bit, 2 lignes, 5x8
        SendCommand(0x0C); // display ON, cursor OFF
        SendCommand(0x06); // entrée gauche→droite
        SendCommand(0x01); // clear
        Thread.Sleep(2);
    }

    public void Clear() { SendCommand(0x01); Thread.Sleep(2); }

    public void SetCursor(int col, int row)
    {
        int[] offsets = [0x00, 0x40];
        SendCommand((byte)(0x80 | (col + offsets[row & 1])));
    }

    public void Print(string text)
    {
        foreach (char c in text) SendChar((byte)c);
    }

    public void SetBacklight(bool on)
    {
        _backlight = on;
        _device.WriteByte((byte)(_backlight ? LCD_BACKLIGHT : 0));
    }

    private void SendCommand(byte cmd) => SendByte(cmd, 0);
    private void SendChar(byte data) => SendByte(data, RS_DATA);

    private void SendByte(byte val, byte mode)
    {
        byte hi = (byte)(val & 0xF0);
        byte lo = (byte)((val << 4) & 0xF0);
        WriteFourBits((byte)(hi | mode));
        WriteFourBits((byte)(lo | mode));
    }

    private void WriteFourBits(byte val)
    {
        byte bl = _backlight ? LCD_BACKLIGHT : (byte)0;
        _device.WriteByte((byte)(val | bl | ENABLE));
        Thread.Sleep(1);
        _device.WriteByte((byte)((val | bl) & ~ENABLE));
    }

    public void Dispose() => _device.Dispose();
}
```

- [ ] **Étape 3 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 4 : Commit**

```bash
git add src/PiscineController/Hardware/Pcf8574.cs src/PiscineController/Hardware/Lcd1602.cs
git commit -m "feat: Pcf8574 relay expander + Lcd1602 I2C display"
```

---

### Tâche 7 : Ds18b20 — sonde température 1-Wire

**Fichiers :**
- Créer : `src/PiscineController/Hardware/Ds18b20.cs`

- [ ] **Étape 1 : Écrire `Hardware/Ds18b20.cs`**

```csharp
using Microsoft.Extensions.Logging;

namespace PiscineController.Hardware;

/// <summary>
/// Lecture DS18B20 via sysfs 1-Wire (/sys/bus/w1/devices).
/// Pas de lib externe — zéro réflexion, AOT-safe.
/// </summary>
public sealed class Ds18b20
{
    private readonly ILogger<Ds18b20> _logger;
    private string? _devicePath;

    public Ds18b20(ILogger<Ds18b20> logger, string? sensorId = null)
    {
        _logger = logger;
        _devicePath = sensorId != null
            ? $"/sys/bus/w1/devices/{sensorId}/w1_slave"
            : FindDevice();
    }

    private string? FindDevice()
    {
        const string base_ = "/sys/bus/w1/devices";
        if (!Directory.Exists(base_)) return null;
        var dir = Directory.GetDirectories(base_, "28-*").FirstOrDefault();
        if (dir == null) { _logger.LogWarning("DS18B20: aucun capteur 1-Wire trouvé"); return null; }
        _logger.LogInformation("DS18B20: capteur détecté {Dir}", dir);
        return Path.Combine(dir, "w1_slave");
    }

    /// <summary>Retourne la température en °C, ou null si lecture échouée.</summary>
    public double? Read()
    {
        if (_devicePath == null) return null;
        try
        {
            string content = File.ReadAllText(_devicePath);
            // Format : "... YES\n...t=12345"
            if (!content.Contains("YES")) return null;
            int idx = content.IndexOf("t=", StringComparison.Ordinal);
            if (idx < 0) return null;
            string raw = content[(idx + 2)..].Trim();
            if (int.TryParse(raw, out int milliC))
                return milliC / 1000.0;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DS18B20: erreur lecture {Path}", _devicePath);
            return null;
        }
    }
}
```

- [ ] **Étape 2 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 3 : Commit**

```bash
git add src/PiscineController/Hardware/Ds18b20.cs
git commit -m "feat: Ds18b20 1-Wire temperature sensor (sysfs, AOT-safe)"
```

---

### Tâche 8 : Wk600Drive — client Modbus RTU minimal (sans FluentModbus)

**Fichiers :**
- Créer : `src/PiscineController/Hardware/Wk600Drive.cs`

> Client Modbus RTU ~120 lignes sur `System.IO.Ports.SerialPort`. Zéro réflexion, AOT-safe.
> Registres WK600-D : CONTROL=0x2000, FREQ_SETPOINT=0x2001, bloc statut 0x3000-0x300B.

- [ ] **Étape 1 : Ajouter `System.IO.Ports` au csproj**

Ajouter dans `src/PiscineController/PiscineController.csproj` :
```xml
<PackageReference Include="System.IO.Ports" Version="9.*" />
```

- [ ] **Étape 2 : Écrire `Hardware/Wk600Drive.cs`**

```csharp
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using PiscineController.Config;

namespace PiscineController.Hardware;

public sealed class Wk600Drive : IDisposable
{
    private readonly SerialPort _port;
    private readonly byte _slaveId;
    private readonly PoolConfig _cfg;
    private readonly ILogger<Wk600Drive> _logger;
    private double _currentFreq;
    private bool _running;
    private readonly object _lock = new();

    private static readonly Dictionary<int, string> FaultLabels = new()
    {
        [0]="Aucun", [1]="Surintensité accel", [2]="Surintensité décel",
        [3]="Surintensité const", [4]="Surtension accel", [5]="Surtension décel",
        [6]="Surtension const", [7]="Sous-tension DC", [8]="Surchauffe variateur",
        [9]="Surchauffe moteur", [10]="Surcharge variateur", [11]="Surcharge moteur",
        [12]="Entrée externe", [13]="Communication", [14]="Perte phase entrée",
        [15]="Perte phase sortie", [16]="EEPROM", [17]="CPU", [18]="Court-circuit sortie",
    };

    public Wk600Drive(PoolConfig cfg, ILogger<Wk600Drive> logger)
    {
        _cfg = cfg;
        _logger = logger;
        _slaveId = (byte)cfg.ModbusSlaveId;
        _port = new SerialPort(cfg.ModbusPort, cfg.ModbusBaudrate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        _port.Open();
        _logger.LogInformation("WK600-D ouvert sur {Port}", cfg.ModbusPort);
    }

    public bool IsRunning => _running;
    public double CurrentFreq => _currentFreq;

    // ── Modbus RTU minimal ──────────────────────────────────────

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) == 1 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    private bool WriteRegister(ushort addr, ushort value)
    {
        lock (_lock)
        {
            Span<byte> req = stackalloc byte[8];
            req[0] = _slaveId; req[1] = 0x06;
            req[2] = (byte)(addr >> 8); req[3] = (byte)(addr & 0xFF);
            req[4] = (byte)(value >> 8); req[5] = (byte)(value & 0xFF);
            ushort crc = Crc16(req[..6]);
            req[6] = (byte)(crc & 0xFF); req[7] = (byte)(crc >> 8);
            _port.Write(req.ToArray(), 0, 8);

            Span<byte> resp = stackalloc byte[8];
            int read = 0;
            while (read < 8) read += _port.Read(resp.ToArray(), read, 8 - read);
            return resp[0] == _slaveId && resp[1] == 0x06;
        }
    }

    private ushort[]? ReadHoldingRegisters(ushort addr, ushort count)
    {
        lock (_lock)
        {
            Span<byte> req = stackalloc byte[8];
            req[0] = _slaveId; req[1] = 0x03;
            req[2] = (byte)(addr >> 8); req[3] = (byte)(addr & 0xFF);
            req[4] = (byte)(count >> 8); req[5] = (byte)(count & 0xFF);
            ushort crc = Crc16(req[..6]);
            req[6] = (byte)(crc & 0xFF); req[7] = (byte)(crc >> 8);
            _port.Write(req.ToArray(), 0, 8);

            int expected = 5 + count * 2;
            byte[] resp = new byte[expected];
            int read = 0;
            while (read < expected) read += _port.Read(resp, read, expected - read);
            if (resp[0] != _slaveId || resp[1] != 0x03) return null;

            var regs = new ushort[count];
            for (int i = 0; i < count; i++)
                regs[i] = (ushort)((resp[3 + i * 2] << 8) | resp[4 + i * 2]);
            return regs;
        }
    }

    // ── Commandes ────────────────────────────────────────────────

    private void SetFreqRaw(double hz)
    {
        double clamped = Math.Clamp(hz, _cfg.FreqMinAbsolute, _cfg.FreqNominal);
        WriteRegister(0x2001, (ushort)(clamped * 100));
        _currentFreq = clamped;
    }

    public async Task RampStartAsync(double targetHz, CancellationToken ct)
    {
        if (_running) return;
        double target = Math.Max(Math.Clamp(targetHz, _cfg.FreqMinAbsolute, _cfg.FreqNominal), _cfg.FreqStartMin);
        SetFreqRaw(_cfg.FreqStartMin);
        WriteRegister(0x2000, 0x0001);
        _running = true;
        _currentFreq = _cfg.FreqStartMin;
        double freq = _cfg.FreqStartMin;
        while (freq < target && !ct.IsCancellationRequested)
        {
            freq = Math.Min(freq + _cfg.FreqRampStep, target);
            SetFreqRaw(freq);
            await Task.Delay((int)(_cfg.FreqRampDelay * 1000), ct).ConfigureAwait(false);
        }
        _logger.LogInformation("WK600-D démarré @ {Freq} Hz", _currentFreq);
    }

    public async Task RampToAsync(double targetHz, CancellationToken ct)
    {
        if (!_running) return;
        double target = Math.Clamp(targetHz, _cfg.FreqMinAbsolute, _cfg.FreqNominal);
        if (Math.Abs(target - _currentFreq) < 0.5) return;
        double freq = _currentFreq;
        double dir = target > freq ? 1 : -1;
        while (dir > 0 ? freq < target : freq > target)
        {
            freq = Math.Clamp(freq + dir * _cfg.FreqRampStep, _cfg.FreqMinAbsolute, _cfg.FreqNominal);
            if (dir > 0 && freq > target) freq = target;
            if (dir < 0 && freq < target) freq = target;
            SetFreqRaw(freq);
            await Task.Delay((int)(_cfg.FreqRampDelay * 1000), ct).ConfigureAwait(false);
        }
        _logger.LogInformation("WK600-D fréquence → {Freq} Hz", _currentFreq);
    }

    public async Task RampStopAsync(CancellationToken ct)
    {
        if (!_running) return;
        double freq = _currentFreq;
        while (freq > _cfg.FreqStartMin && !ct.IsCancellationRequested)
        {
            freq = Math.Max(freq - _cfg.FreqRampStep, _cfg.FreqStartMin);
            SetFreqRaw(freq);
            await Task.Delay((int)(_cfg.FreqRampDelay * 1000), ct).ConfigureAwait(false);
        }
        WriteRegister(0x2000, 0x0005);
        _running = false;
        _currentFreq = 0;
        _logger.LogInformation("WK600-D arrêté");
    }

    public void FaultReset() => WriteRegister(0x2000, 0x0080);

    public DriveStatusSnapshot ReadStatus()
    {
        var regs = ReadHoldingRegisters(0x3000, 12);
        if (regs == null || regs.Length < 12)
            return new DriveStatusSnapshot { SetpointHz = _currentFreq };

        ushort sw = regs[8];
        bool isRunning = (sw & 1) != 0;
        bool isFault   = (sw & (1 << 3)) != 0;
        bool atSetpoint= (sw & (1 << 7)) != 0;
        _running = isRunning;

        return new DriveStatusSnapshot
        {
            OutFreqHz    = regs[0] / 100.0,
            OutCurrentA  = regs[1] / 10.0,
            OutVoltageV  = regs[2],
            DcBusV       = regs[3],
            OutPowerKw   = regs[4] / 10.0,
            DriveTempC   = regs[5],
            RunTimeH     = regs[6],
            FaultCode    = regs[7],
            FaultLabel   = FaultLabels.TryGetValue(regs[7], out var lbl) ? lbl : $"Code {regs[7]}",
            MotorRpm     = regs[9],
            InFreqHz     = regs[10] / 100.0,
            InVoltageV   = regs[11],
            IsRunning    = isRunning,
            IsFault      = isFault,
            AtSetpoint   = atSetpoint,
            SetpointHz   = _currentFreq,
        };
    }

    public void Dispose()
    {
        try { _port.Close(); _port.Dispose(); } catch { }
    }
}

public sealed class DriveStatusSnapshot
{
    public double OutFreqHz { get; init; }
    public double OutCurrentA { get; init; }
    public double OutVoltageV { get; init; }
    public double DcBusV { get; init; }
    public double OutPowerKw { get; init; }
    public double DriveTempC { get; init; }
    public int RunTimeH { get; init; }
    public int FaultCode { get; init; }
    public string FaultLabel { get; init; } = "Aucun";
    public int MotorRpm { get; init; }
    public double InFreqHz { get; init; }
    public double InVoltageV { get; init; }
    public bool IsRunning { get; init; }
    public bool IsFault { get; init; }
    public bool AtSetpoint { get; init; }
    public double SetpointHz { get; init; }
}
```

- [ ] **Étape 3 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 4 : Commit**

```bash
git add src/PiscineController/Hardware/Wk600Drive.cs src/PiscineController/PiscineController.csproj
git commit -m "feat: Wk600Drive — client Modbus RTU minimal AOT-safe"
```

---

### Tâche 9 : GpioButtons

**Fichiers :**
- Créer : `src/PiscineController/Hardware/GpioButtons.cs`

- [ ] **Étape 1 : Écrire `Hardware/GpioButtons.cs`**

```csharp
using System.Device.Gpio;
using Microsoft.Extensions.Logging;
using PiscineController.Config;

namespace PiscineController.Hardware;

public sealed class GpioButtons : IDisposable
{
    private readonly GpioController _gpio;
    private readonly PoolConfig _cfg;
    private readonly ILogger<GpioButtons> _logger;

    public event Action? LcdDisplayPressed;
    public event Action? PrimePumpPressed;
    public event Action? PauseFilterPressed;
    public event Action? ResumeFilterPressed;

    public GpioButtons(PoolConfig cfg, ILogger<GpioButtons> logger)
    {
        _cfg = cfg;
        _logger = logger;
        _gpio = new GpioController(PinNumberingScheme.Logical);

        Register(cfg.BtnLcdDisplay,   () => LcdDisplayPressed?.Invoke());
        Register(cfg.BtnPrimePump,    () => PrimePumpPressed?.Invoke());
        Register(cfg.BtnPauseFilter,  () => PauseFilterPressed?.Invoke());
        Register(cfg.BtnResumeFilter, () => ResumeFilterPressed?.Invoke());
    }

    private void Register(int pin, Action handler)
    {
        _gpio.OpenPin(pin, PinMode.InputPullUp);
        _gpio.RegisterCallbackForPinValueChangedEvent(
            pin, PinEventTypes.Falling,
            (_, args) =>
            {
                _logger.LogDebug("Bouton GPIO {Pin} appuyé", args.PinNumber);
                handler();
            });
    }

    public void Dispose()
    {
        _gpio.Dispose();
    }
}
```

- [ ] **Étape 2 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 3 : Commit**

```bash
git add src/PiscineController/Hardware/GpioButtons.cs
git commit -m "feat: GpioButtons — 4 boutons GPIO avec interruptions"
```

---

### Tâche 10 : MqttService + HaDiscovery

**Fichiers :**
- Créer : `src/PiscineController/Services/MqttService.cs`

- [ ] **Étape 1 : Écrire `Services/MqttService.cs`**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
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
        var factory = new MqttFactory();
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
        string payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
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
                case "ph_target":
                    break; // PhPidController lit depuis PoolConfig, reconfiguration runtime non supportée
                case "electrolyzer":
                    // géré par ElectrolyzerService via PoolState
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
            await _client.DisconnectAsync(cancellationToken: ct);
        }
        await base.StopAsync(ct);
    }
}
```

- [ ] **Étape 2 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 3 : Commit**

```bash
git add src/PiscineController/Services/MqttService.cs
git commit -m "feat: MqttService — MQTTnet v5, reconnexion, LWT, HA autodiscovery"
```

---

### Tâche 11 : FiltrationService (boucle 5s)

**Fichiers :**
- Créer : `src/PiscineController/Services/FiltrationService.cs`

> `FiltrationService` orchestre `FiltrationManager` (logique pure) + `Wk600Drive` (hardware).
> Boucle 5s : rebuild schedule si nouveau jour, décide démarrage/arrêt pompe, applique rampes.

- [ ] **Étape 1 : Écrire `Services/FiltrationService.cs`**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;

namespace PiscineController.Services;

public sealed class FiltrationService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly FiltrationManager _filtration;
    private readonly Wk600Drive _drive;
    private readonly ILogger<FiltrationService> _logger;

    public FiltrationService(PoolConfig cfg, PoolState state,
        FiltrationManager filtration, Wk600Drive drive,
        ILogger<FiltrationService> logger)
    {
        _cfg = cfg; _state = state;
        _filtration = filtration; _drive = drive; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Rebuild planning si nouveau jour ou première exécution
                if (_filtration.Mode == FilterMode.Auto && _filtration.NeedsRebuild())
                    _filtration.BuildSchedule(_state.WaterTempC, _filtration.TargetFreqHz);

                bool shouldRun = _filtration.ShouldPumpRun();
                double targetFreq = _filtration.GetRunFreq();

                if (shouldRun && !_drive.IsRunning)
                {
                    _logger.LogInformation("Démarrage pompe @ {Freq} Hz", targetFreq);
                    await _drive.RampStartAsync(targetFreq, ct);
                    _state.PumpRunning = true;
                }
                else if (!shouldRun && _drive.IsRunning)
                {
                    _logger.LogInformation("Arrêt pompe");
                    await _drive.RampStopAsync(ct);
                    _state.PumpRunning = false;
                }
                else if (shouldRun && _drive.IsRunning
                      && Math.Abs(targetFreq - _drive.CurrentFreq) > 2.0
                      && _filtration.Mode == FilterMode.Forced)
                {
                    await _drive.RampToAsync(targetFreq, ct);
                }

                _state.PumpFreqHz = _drive.CurrentFreq;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "FiltrationService: erreur boucle");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }

        // Arrêt propre
        if (_drive.IsRunning)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _drive.RampStopAsync(cts.Token);
        }
    }
}
```

- [ ] **Étape 2 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 3 : Commit**

```bash
git add src/PiscineController/Services/FiltrationService.cs
git commit -m "feat: FiltrationService — orchestre FiltrationManager + Wk600Drive"
```

---

### Tâche 12 : SensorService (boucle 60s)

**Fichiers :**
- Créer : `src/PiscineController/Services/SensorService.cs`

- [ ] **Étape 1 : Écrire `Services/SensorService.cs`**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;
using PiscineController.Ph;
using System.Text.Json;

namespace PiscineController.Services;

public sealed class SensorService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly EzoPh _ph;
    private readonly EzoOrp _orp;
    private readonly EzoRtd _rtd;
    private readonly EzoPmp _pmp;
    private readonly FiltrationManager _filtration;
    private readonly PhPidController _pid;
    private readonly MqttService _mqtt;
    private readonly ILogger<SensorService> _logger;

    public SensorService(PoolConfig cfg, PoolState state,
        EzoPh ph, EzoOrp orp, EzoRtd rtd, EzoPmp pmp,
        FiltrationManager filtration, PhPidController pid,
        MqttService mqtt, ILogger<SensorService> logger)
    {
        _cfg = cfg; _state = state;
        _ph = ph; _orp = orp; _rtd = rtd; _pmp = pmp;
        _filtration = filtration; _pid = pid;
        _mqtt = mqtt; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ReadAndPublish(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "SensorService: erreur lecture capteurs"); }

            await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
    }

    private async Task ReadAndPublish(CancellationToken ct)
    {
        // Lecture température pour pH/ORP (compensation thermique Atlas EZO)
        double? tempC = _rtd.Read();
        if (tempC.HasValue)
        {
            _state.WaterTempC = tempC.Value;
            _ph.SendCommand($"T,{tempC.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 300);
            _orp.SendCommand($"T,{tempC.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 300);
        }

        double? phVal = _ph.Read();
        double? orpMv = _orp.Read();

        if (phVal.HasValue) _state.PhValue = phVal.Value;
        if (orpMv.HasValue) _state.OrpMv = orpMv.Value;

        // pH PID → dosage
        if (phVal.HasValue)
        {
            double dose = _pid.Compute(phVal.Value, _state.PumpRunning);
            if (dose > 0)
            {
                _logger.LogInformation("Dosage pH: {Dose} mL (pH={Ph:F2})", dose, phVal.Value);
                _pmp.Dose(dose);
            }
        }

        // ORP → fréquence filtration
        if (orpMv.HasValue)
        {
            var (waterState, targetFreq, alarm) = _filtration.UpdateOrp(orpMv.Value);
            _filtration.SetTargetFreq(targetFreq);
            _filtration.CheckBoostExit(orpMv.Value);
            if (alarm) _logger.LogWarning("Alarme ORP: {Orp} mV — état {State}", orpMv.Value, waterState);
        }

        var payload = new SensorPayload(
            PhValue: phVal ?? _state.PhValue,
            OrpMv: orpMv ?? _state.OrpMv,
            WaterTempC: tempC ?? _state.WaterTempC,
            WaterState: _filtration.WaterState.ToString(),
            TargetFreqHz: _filtration.TargetFreqHz,
            OrpAlarm: _filtration.WaterState is WaterState.CriticalLow or WaterState.Overdose);

        await _mqtt.PublishAsync(
            $"{_cfg.MqttPrefix}/sensors",
            JsonSerializer.Serialize(payload, AppJsonContext.Default.SensorPayload),
            ct: ct);
    }
}
```

- [ ] **Étape 2 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 3 : Commit**

```bash
git add src/PiscineController/Services/SensorService.cs
git commit -m "feat: SensorService — Atlas EZO 60s + pH PID + ORP → filtration"
```

---

### Tâche 13 : DriveService + PumpTempService (boucle 30s)

**Fichiers :**
- Créer : `src/PiscineController/Services/DriveService.cs`
- Créer : `src/PiscineController/Services/PumpTempService.cs`

- [ ] **Étape 1 : Écrire `Services/DriveService.cs`**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Hardware;
using System.Text.Json;

namespace PiscineController.Services;

public sealed class DriveService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly Wk600Drive _drive;
    private readonly MqttService _mqtt;
    private readonly ILogger<DriveService> _logger;

    public DriveService(PoolConfig cfg, PoolState state,
        Wk600Drive drive, MqttService mqtt, ILogger<DriveService> logger)
    {
        _cfg = cfg; _state = state; _drive = drive; _mqtt = mqtt; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = _drive.ReadStatus();
                _state.PumpRunning = snap.IsRunning;
                _state.PumpFreqHz = snap.OutFreqHz;

                if (snap.IsFault)
                    _logger.LogWarning("WK600-D défaut: [{Code}] {Label}", snap.FaultCode, snap.FaultLabel);

                var payload = new DriveStatusPayload(
                    snap.OutFreqHz, snap.OutCurrentA, snap.OutVoltageV,
                    snap.OutPowerKw, snap.DriveTempC, snap.RunTimeH,
                    snap.FaultCode, snap.FaultLabel,
                    snap.IsRunning, snap.IsFault, snap.AtSetpoint, snap.SetpointHz);

                await _mqtt.PublishAsync(
                    $"{_cfg.MqttPrefix}/drive",
                    JsonSerializer.Serialize(payload, AppJsonContext.Default.DriveStatusPayload),
                    ct: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "DriveService: erreur lecture variateur"); }

            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Étape 2 : Écrire `Services/PumpTempService.cs`**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Hardware;

namespace PiscineController.Services;

public sealed class PumpTempService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly Ds18b20 _sensor;
    private readonly MqttService _mqtt;
    private readonly ILogger<PumpTempService> _logger;

    public PumpTempService(PoolConfig cfg, PoolState state,
        Ds18b20 sensor, MqttService mqtt, ILogger<PumpTempService> logger)
    {
        _cfg = cfg; _state = state; _sensor = sensor; _mqtt = mqtt; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                double? tempC = _sensor.Read();
                if (tempC.HasValue)
                {
                    if (tempC >= _cfg.PumpTempCriticalC)
                        _logger.LogCritical("Température pompe CRITIQUE: {T}°C — arrêt requis", tempC);
                    else if (tempC >= _cfg.PumpTempAlertC)
                        _logger.LogWarning("Température pompe élevée: {T}°C", tempC);

                    await _mqtt.PublishAsync(
                        $"{_cfg.MqttPrefix}/pump_temp",
                        tempC.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ct: ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "PumpTempService: erreur lecture DS18B20"); }

            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Étape 3 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 4 : Commit**

```bash
git add src/PiscineController/Services/DriveService.cs src/PiscineController/Services/PumpTempService.cs
git commit -m "feat: DriveService + PumpTempService — télémétrie 30s"
```

---

### Tâche 14 : ElectrolyzerService + ButtonService + DisplayService

**Fichiers :**
- Créer : `src/PiscineController/Services/ElectrolyzerService.cs`
- Créer : `src/PiscineController/Services/ButtonService.cs`
- Créer : `src/PiscineController/Services/DisplayService.cs`

- [ ] **Étape 1 : Écrire `Services/ElectrolyzerService.cs`**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Hardware;

namespace PiscineController.Services;

public sealed class ElectrolyzerService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly Pcf8574 _relay;
    private readonly ILogger<ElectrolyzerService> _logger;
    private bool _electrolyzerOn;

    public ElectrolyzerService(PoolConfig cfg, PoolState state,
        Pcf8574 relay, ILogger<ElectrolyzerService> logger)
    {
        _cfg = cfg; _state = state; _relay = relay; _logger = logger;
    }

    public void SetElectrolyzer(bool on)
    {
        _electrolyzerOn = on;
        Apply();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Apply(); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "ElectrolyzerService: erreur relais"); }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        // Arrêt propre : relais OFF
        try { _relay.SetPin(0, false); } catch { }
    }

    private void Apply()
    {
        // Électrolyseur actif seulement si pompe tourne et demande active
        bool active = _electrolyzerOn && _state.PumpRunning;
        _relay.SetPin(0, active);
    }
}
```

- [ ] **Étape 2 : Écrire `Services/DisplayService.cs`**

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Hardware;

namespace PiscineController.Services;

public sealed record DisplayCommand(string Line1, string Line2, int DurationMs = 5000);

public sealed class DisplayService : BackgroundService
{
    private readonly Lcd1602 _lcd;
    private readonly ILogger<DisplayService> _logger;
    private readonly Channel<DisplayCommand> _channel =
        Channel.CreateBounded<DisplayCommand>(new BoundedChannelOptions(4)
        { FullMode = BoundedChannelFullMode.DropOldest });

    public DisplayService(Lcd1602 lcd, ILogger<DisplayService> logger)
    {
        _lcd = lcd; _logger = logger;
    }

    public void Show(string line1, string line2, int durationMs = 5000) =>
        _channel.Writer.TryWrite(new DisplayCommand(line1, line2, durationMs));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var cmd in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                _lcd.Clear();
                _lcd.SetCursor(0, 0); _lcd.Print(cmd.Line1.PadRight(16)[..16]);
                _lcd.SetCursor(0, 1); _lcd.Print(cmd.Line2.PadRight(16)[..16]);
                _lcd.SetBacklight(true);
                await Task.Delay(cmd.DurationMs, ct);
                _lcd.SetBacklight(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "DisplayService: erreur LCD"); }
        }
    }
}
```

- [ ] **Étape 3 : Écrire `Services/ButtonService.cs`**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;
using PiscineController.Ph;

namespace PiscineController.Services;

public sealed class ButtonService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly GpioButtons _buttons;
    private readonly FiltrationManager _filtration;
    private readonly EzoPmp _pmp;
    private readonly DisplayService _display;
    private readonly ILogger<ButtonService> _logger;

    public ButtonService(PoolConfig cfg, PoolState state,
        GpioButtons buttons, FiltrationManager filtration,
        EzoPmp pmp, DisplayService display, ILogger<ButtonService> logger)
    {
        _cfg = cfg; _state = state; _buttons = buttons;
        _filtration = filtration; _pmp = pmp; _display = display; _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        _buttons.LcdDisplayPressed   += OnLcdDisplay;
        _buttons.PrimePumpPressed    += OnPrimePump;
        _buttons.PauseFilterPressed  += OnPauseFilter;
        _buttons.ResumeFilterPressed += OnResumeFilter;
        return Task.Delay(Timeout.Infinite, ct);
    }

    private void OnLcdDisplay()
    {
        _logger.LogDebug("Bouton LCD Display");
        _display.Show(
            $"pH{_state.PhValue:F2} ORP{_state.OrpMv:F0}",
            $"T{_state.WaterTempC:F1}C {_state.PumpFreqHz:F0}Hz",
            _cfg.LcdDisplayDuration * 1000);
    }

    private void OnPrimePump()
    {
        _logger.LogInformation("Amorçage pompe doseuse {Vol} mL", _cfg.PrimeVolumeMl);
        _display.Show("Amorcage pompe", $"{_cfg.PrimeVolumeMl} mL...", 3000);
        Task.Run(() => _pmp.Dose(_cfg.PrimeVolumeMl));
    }

    private void OnPauseFilter()
    {
        _logger.LogInformation("Pause filtration (bouton)");
        _filtration.SetMode("pause");
        _state.FilterMode = FilterMode.Pause;
        _display.Show("Filtration", "EN PAUSE", 3000);
    }

    private void OnResumeFilter()
    {
        _logger.LogInformation("Reprise filtration auto (bouton)");
        _filtration.ResumeAuto();
        _state.FilterMode = FilterMode.Auto;
        _display.Show("Filtration", "AUTO", 3000);
    }
}
```

- [ ] **Étape 4 : Build**

```bash
dotnet build src/PiscineController/
```
Résultat attendu : `Build succeeded.`

- [ ] **Étape 5 : Commit**

```bash
git add src/PiscineController/Services/ElectrolyzerService.cs \
        src/PiscineController/Services/ButtonService.cs \
        src/PiscineController/Services/DisplayService.cs
git commit -m "feat: ElectrolyzerService + ButtonService + DisplayService"
```

---

### Tâche 15 : Program.cs — câblage DI

**Fichiers :**
- Modifier : `src/PiscineController/Program.cs`

- [ ] **Étape 1 : Écrire `Program.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;
using PiscineController.Ph;
using PiscineController.Services;
using System.Text.Json;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .ConfigureServices((ctx, services) =>
    {
        // Config
        var poolConfig = JsonSerializer.Deserialize(
            ctx.Configuration.GetSection("Pool").Get<string>() ?? "{}",
            AppJsonContext.Default.PoolConfig) ?? new PoolConfig();

        // Lire appsettings.json manuellement pour AOT (pas de Microsoft.Extensions.Configuration.Binder reflection)
        var cfgJson = File.Exists("appsettings.json")
            ? File.ReadAllText("appsettings.json")
            : "{}";
        using var doc = JsonDocument.Parse(cfgJson);
        var poolSection = doc.RootElement.TryGetProperty("Pool", out var p)
            ? p.GetRawText() : "{}";
        poolConfig = JsonSerializer.Deserialize(poolSection, AppJsonContext.Default.PoolConfig) ?? new PoolConfig();

        services.AddSingleton(poolConfig);
        services.AddSingleton<PoolState>();

        // Hardware (singletons)
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new EzoPh(cfg.I2cBus, cfg.AtlasPhAddr, sp.GetRequiredService<ILogger<EzoPh>>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new EzoOrp(cfg.I2cBus, cfg.AtlasOrpAddr, sp.GetRequiredService<ILogger<EzoOrp>>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new EzoRtd(cfg.I2cBus, cfg.AtlasRtdAddr, sp.GetRequiredService<ILogger<EzoRtd>>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new EzoPmp(cfg.I2cBus, cfg.AtlasPmpAddr, sp.GetRequiredService<ILogger<EzoPmp>>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new Pcf8574(cfg.I2cBus, cfg.Pcf8574Addr);
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new Lcd1602(cfg.I2cBus, cfg.LcdI2cAddr);
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new Ds18b20(sp.GetRequiredService<ILogger<Ds18b20>>(), cfg.OnewirePumpSensorId);
        });
        services.AddSingleton(sp =>
            new Wk600Drive(sp.GetRequiredService<PoolConfig>(), sp.GetRequiredService<ILogger<Wk600Drive>>()));
        services.AddSingleton(sp =>
            new GpioButtons(sp.GetRequiredService<PoolConfig>(), sp.GetRequiredService<ILogger<GpioButtons>>()));

        // Logique pure
        services.AddSingleton(sp => new FiltrationManager(sp.GetRequiredService<PoolConfig>()));
        services.AddSingleton(sp => new PhPidController(sp.GetRequiredService<PoolConfig>()));

        // Services (ordre DI = ordre arrêt inversé)
        services.AddHostedService<DisplayService>();
        services.AddHostedService<ElectrolyzerService>();
        services.AddHostedService<PumpTempService>();
        services.AddHostedService<DriveService>();
        services.AddHostedService<FiltrationService>();
        services.AddHostedService<SensorService>();
        services.AddHostedService<ButtonService>();
        services.AddHostedService<MqttService>();
    })
    .Build();

await host.RunAsync();
```

- [ ] **Étape 2 : Build complet**

```bash
dotnet build
```
Résultat attendu : `Build succeeded.` (src + tests)

- [ ] **Étape 3 : Tests complets**

```bash
dotnet test
```
Résultat attendu : tous les tests `PASS`.

- [ ] **Étape 4 : Commit**

```bash
git add src/PiscineController/Program.cs
git commit -m "feat: Program.cs — câblage DI complet"
```

---

### Tâche 16 : systemd unit file

**Fichiers :**
- Créer : `systemd/piscine-controller.service`

- [ ] **Étape 1 : Écrire `systemd/piscine-controller.service`**

```ini
[Unit]
Description=Piscine Controller (.NET 10 AOT)
After=network.target

[Service]
Type=simple
User=pi
WorkingDirectory=/opt/piscine
ExecStart=/opt/piscine/PiscineController
Restart=on-failure
RestartSec=5s
KillSignal=SIGTERM
TimeoutStopSec=15s
StandardOutput=journal
StandardError=journal
SyslogIdentifier=piscine-controller

[Install]
WantedBy=multi-user.target
```

- [ ] **Étape 2 : Documenter le déploiement dans le README (section)**

Ajouter dans `README.md` :

```markdown
## Déploiement

### Publication cross-compile (dev Windows → RPi 3B+)
```bash
dotnet publish src/PiscineController/ -c Release -r linux-arm --self-contained \
  /p:PublishAot=true -o publish/
```

### Installation sur RPi
```bash
sudo mkdir -p /opt/piscine
sudo cp publish/PiscineController /opt/piscine/
sudo cp src/PiscineController/appsettings.json /opt/piscine/
sudo cp systemd/piscine-controller.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now piscine-controller
sudo journalctl -u piscine-controller -f
```
```

- [ ] **Étape 3 : Commit**

```bash
git add systemd/piscine-controller.service README.md
git commit -m "feat: systemd unit + README déploiement"
```

---

### Tâche 17 : GitHub Actions (ci.yml, publish.yml, security.yml, dependabot)

**Fichiers :**
- Créer : `.github/workflows/ci.yml`
- Créer : `.github/workflows/publish.yml`
- Créer : `.github/workflows/security.yml`
- Créer : `.github/workflows/dependabot-auto-merge.yml`
- Créer : `.github/dependabot.yml`

- [ ] **Étape 1 : Écrire `.github/workflows/ci.yml`**

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-build -c Release --logger trx
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results
          path: '**/*.trx'
```

- [ ] **Étape 2 : Écrire `.github/workflows/publish.yml`**

```yaml
name: Publish

on:
  push:
    tags:
      - '[0-9]+.[0-9]+.[0-9]+.[0-9]+'

jobs:
  validate-version:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Vérifier version == tag
        run: |
          TAG="${GITHUB_REF_NAME}"
          VERSION=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props)
          if [ "$TAG" != "$VERSION" ]; then
            echo "Tag $TAG != Version $VERSION dans Directory.Build.props"
            exit 1
          fi

  publish-arm:
    needs: validate-version
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Installer cross-compile toolchain
        run: |
          sudo apt-get update -qq
          sudo apt-get install -y clang gcc-arm-linux-gnueabihf zlib1g-dev
      - name: Publish AOT linux-arm
        run: |
          dotnet publish src/PiscineController/ \
            -c Release -r linux-arm --self-contained \
            /p:PublishAot=true \
            -o publish/
      - name: Créer GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          files: publish/PiscineController
          generate_release_notes: true
```

- [ ] **Étape 3 : Écrire `.github/workflows/security.yml`**

```yaml
name: Security

on:
  schedule:
    - cron: '0 8 * * 1'  # Lundi 08h00 UTC
  workflow_dispatch:

jobs:
  nuget-audit:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore
      - name: Audit NuGet
        run: dotnet list package --vulnerable --include-transitive 2>&1 | tee audit.txt
      - name: Fail si vulnérabilités
        run: |
          if grep -q "has the following vulnerable packages" audit.txt; then
            echo "Vulnérabilités NuGet détectées"
            exit 1
          fi

  trivy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: aquasecurity/trivy-action@master
        with:
          image-ref: 'mcr.microsoft.com/dotnet/runtime-deps:10.0-bookworm-slim-arm32v7'
          format: 'sarif'
          output: 'trivy.sarif'
      - uses: github/codeql-action/upload-sarif@v3
        with:
          sarif_file: 'trivy.sarif'
```

- [ ] **Étape 4 : Écrire `.github/workflows/dependabot-auto-merge.yml`**

```yaml
name: Dependabot Auto-merge

on: pull_request

permissions:
  contents: write
  pull-requests: write

jobs:
  auto-merge:
    runs-on: ubuntu-latest
    if: github.actor == 'dependabot[bot]'
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
          token: ${{ secrets.GITHUB_TOKEN }}
      - uses: dependabot/fetch-metadata@v2
        id: meta
      - name: Auto-merge patch/minor
        if: steps.meta.outputs.update-type == 'version-update:semver-patch' || steps.meta.outputs.update-type == 'version-update:semver-minor'
        run: |
          # Incrémenter version patch dans Directory.Build.props
          VERSION=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props)
          IFS='.' read -ra V <<< "$VERSION"
          NEW_PATCH=$((V[3]+1))
          NEW_VERSION="${V[0]}.${V[1]}.${V[2]}.${NEW_PATCH}"
          sed -i "s|<Version>$VERSION</Version>|<Version>$NEW_VERSION</Version>|" Directory.Build.props
          git config user.name "github-actions[bot]"
          git config user.email "github-actions[bot]@users.noreply.github.com"
          git add Directory.Build.props
          git commit -m "chore: bump version to $NEW_VERSION [dependabot auto-merge]"
          git push
          gh pr merge --squash --auto "${{ github.event.pull_request.number }}"
          git tag "$NEW_VERSION"
          git push --tags
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

- [ ] **Étape 5 : Écrire `.github/dependabot.yml`**

```yaml
version: 2
updates:
  - package-ecosystem: nuget
    directory: "/"
    schedule:
      interval: weekly
      day: monday
    groups:
      all-nuget:
        patterns: ["*"]

  - package-ecosystem: github-actions
    directory: "/"
    schedule:
      interval: weekly
      day: monday
    groups:
      all-actions:
        patterns: ["*"]
```

- [ ] **Étape 6 : Commit final**

```bash
git add .github/
git commit -m "feat: GitHub Actions — ci, publish AOT linux-arm, security, dependabot"
```

---

## Validation finale

- [ ] `dotnet build` → `Build succeeded.`
- [ ] `dotnet test` → tous PASS
- [ ] `git log --oneline` → 17 commits feat/chore depuis scaffold
- [ ] Pousser sur GitHub → CI vert
- [ ] Créer tag `1.0.0.0` → publish workflow → binaire `PiscineController` dans GitHub Releases
- [ ] Copier binaire sur RPi, activer systemd → `journalctl -u piscine-controller -f` montre démarrage

