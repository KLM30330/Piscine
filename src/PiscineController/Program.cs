using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;
using PiscineController.Ph;
using PiscineController.Services;

// Forcer le répertoire courant = dossier du binaire
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .ConfigureServices((ctx, services) =>
    {
        var poolConfig = new PoolConfig();
        ctx.Configuration.GetSection("Pool").Bind(poolConfig);

        services.AddSingleton(poolConfig);
        services.AddSingleton(_ => new PoolState());
        services.AddSingleton(sp => new EquipmentHealth(sp.GetRequiredService<ILogger<EquipmentHealth>>()));

        // Hardware (singletons)
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new EzoPh(cfg.I2cBus, cfg.AtlasPhAddr,
                sp.GetRequiredService<ILogger<EzoPh>>(), sp.GetRequiredService<EquipmentHealth>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new EzoOrp(cfg.I2cBus, cfg.AtlasOrpAddr,
                sp.GetRequiredService<ILogger<EzoOrp>>(), sp.GetRequiredService<EquipmentHealth>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new EzoRtd(cfg.I2cBus, cfg.AtlasRtdAddr,
                sp.GetRequiredService<ILogger<EzoRtd>>(), sp.GetRequiredService<EquipmentHealth>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new EzoPmp(cfg.I2cBus, cfg.AtlasPmpAddr,
                sp.GetRequiredService<ILogger<EzoPmp>>(), sp.GetRequiredService<EquipmentHealth>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new Pcf8574(cfg.I2cBus, cfg.Pcf8574Addr,
                sp.GetRequiredService<EquipmentHealth>(), sp.GetRequiredService<ILogger<Pcf8574>>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new Lcd1602(cfg.I2cBus, cfg.LcdI2cAddr, sp.GetRequiredService<EquipmentHealth>());
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<PoolConfig>();
            return new Ds18b20(sp.GetRequiredService<ILogger<Ds18b20>>(),
                sp.GetRequiredService<EquipmentHealth>(), cfg.OnewirePumpSensorId);
        });
        services.AddSingleton(sp =>
            new Wk600Drive(sp.GetRequiredService<PoolConfig>(),
                sp.GetRequiredService<ILogger<Wk600Drive>>(), sp.GetRequiredService<EquipmentHealth>()));
        services.AddSingleton(sp =>
            new GpioButtons(sp.GetRequiredService<PoolConfig>(), sp.GetRequiredService<ILogger<GpioButtons>>()));

        // Logique pure
        services.AddSingleton(sp => new FiltrationManager(sp.GetRequiredService<PoolConfig>()));
        services.AddSingleton(sp => new PhPidController(sp.GetRequiredService<PoolConfig>()));

        // DisplayService — singleton pour que ButtonService puisse le résoudre directement
        services.AddSingleton(sp => new DisplayService(
            sp.GetRequiredService<Lcd1602>(),
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<ILogger<DisplayService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<DisplayService>());

        // PumpPrimingService — singleton partagé entre ButtonService (bouton
        // physique) et MqttService (commande HA), pour un seul verrou anti-
        // double-déclenchement commun aux deux.
        services.AddSingleton(sp => new PumpPrimingService(
            sp.GetRequiredService<EzoPmp>(),
            sp.GetRequiredService<DisplayService>(),
            sp.GetRequiredService<ILogger<PumpPrimingService>>()));

        // MqttService — singleton pour que DriveService/PumpTempService/SensorService le résolvent
        services.AddSingleton(sp => new MqttService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<FiltrationManager>(),
            sp.GetRequiredService<PhPidController>(),
            sp.GetRequiredService<EzoPh>(),
            sp.GetRequiredService<Wk600Drive>(),
            sp.GetRequiredService<PumpPrimingService>(),
            sp.GetRequiredService<ILogger<MqttService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<MqttService>());

        // Autres hosted services — factory lambdas (AOT-safe, pas de reflection)
        services.AddHostedService(sp => new ElectrolyzerService(
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<Pcf8574>(),
            sp.GetRequiredService<MqttService>(),
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<ILogger<ElectrolyzerService>>()));
        services.AddHostedService(sp => new PumpTempService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<Ds18b20>(),
            sp.GetRequiredService<MqttService>(),
            sp.GetRequiredService<ILogger<PumpTempService>>()));
        services.AddHostedService(sp => new DriveService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<Wk600Drive>(),
            sp.GetRequiredService<MqttService>(),
            sp.GetRequiredService<ILogger<DriveService>>()));
        services.AddHostedService(sp => new FiltrationService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<FiltrationManager>(),
            sp.GetRequiredService<Wk600Drive>(),
            sp.GetRequiredService<ILogger<FiltrationService>>()));
        services.AddHostedService(sp => new SensorService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<EzoPh>(),
            sp.GetRequiredService<EzoOrp>(),
            sp.GetRequiredService<EzoRtd>(),
            sp.GetRequiredService<EzoPmp>(),
            sp.GetRequiredService<FiltrationManager>(),
            sp.GetRequiredService<PhPidController>(),
            sp.GetRequiredService<MqttService>(),
            sp.GetRequiredService<ILogger<SensorService>>()));
        services.AddHostedService(sp => new ButtonService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<GpioButtons>(),
            sp.GetRequiredService<FiltrationManager>(),
            sp.GetRequiredService<PumpPrimingService>(),
            sp.GetRequiredService<DisplayService>(),
            sp.GetRequiredService<ILogger<ButtonService>>()));
        services.AddHostedService(sp => new HealthService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<EquipmentHealth>(),
            sp.GetRequiredService<MqttService>(),
            sp.GetRequiredService<ILogger<HealthService>>()));
    })
    .Build();

await host.RunAsync();
