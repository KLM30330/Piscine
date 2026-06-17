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

        // DisplayService — singleton pour que ButtonService puisse le résoudre directement
        services.AddSingleton(sp => new DisplayService(
            sp.GetRequiredService<Lcd1602>(),
            sp.GetRequiredService<ILogger<DisplayService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<DisplayService>());

        // MqttService — singleton pour que DriveService/PumpTempService/SensorService le résolvent
        services.AddSingleton(sp => new MqttService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<FiltrationManager>(),
            sp.GetRequiredService<PhPidController>(),
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
            sp.GetRequiredService<EzoPmp>(),
            sp.GetRequiredService<DisplayService>(),
            sp.GetRequiredService<ILogger<ButtonService>>()));
    })
    .Build();

await host.RunAsync();
