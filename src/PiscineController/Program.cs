using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController;
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
        // Lire appsettings.json manuellement — pas de reflection binder (AOT)
        var cfgJson = File.Exists("appsettings.json")
            ? File.ReadAllText("appsettings.json")
            : "{}";
        using var doc = JsonDocument.Parse(cfgJson);
        var poolSection = doc.RootElement.TryGetProperty("Pool", out var p)
            ? p.GetRawText() : "{}";
        var poolConfig = JsonSerializer.Deserialize(poolSection, AppJsonContext.Default.PoolConfig) ?? new PoolConfig();

        services.AddSingleton(poolConfig);
        services.AddSingleton(_ => new PoolState());

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

        // Services — ordre inversé = ordre d'arrêt
        // DisplayService s'arrête en dernier (LCD clear)
        // MqttService s'arrête en premier (publie "offline" avant libération hardware)
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
