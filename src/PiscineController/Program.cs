using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using PiscineController;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;
using PiscineController.Ph;
using PiscineController.Services;

// Forcer le répertoire courant = dossier du binaire
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Lu en amont, avant la construction du host, car ConfigureLogging a besoin
// du chemin/rétention pour brancher FileLoggerProvider — léger doublon de
// lecture de configuration (le host la relit ensuite normalement), mais
// évite de complexifier l'ordre d'initialisation pour 2 valeurs.
var preConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();
string logDirectory   = preConfig["Pool:LogDirectory"]   ?? "/opt/piscine/logs";
int    logRetentionDays = int.TryParse(preConfig["Pool:LogRetentionDays"], out int rd) ? rd : 7;

var fileLoggerProvider = new FileLoggerProvider(logDirectory, LogLevel.Information, logRetentionDays);

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.AddProvider(fileLoggerProvider);
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddSingleton(fileLoggerProvider);

        var poolConfig = new PoolConfig();
        ctx.Configuration.GetSection("Pool").Bind(poolConfig);

        services.AddSingleton(poolConfig);
        services.AddSingleton(_ => new PoolState());
        services.AddSingleton(sp => new EquipmentHealth(
            sp.GetRequiredService<ILogger<EquipmentHealth>>(),
            sp.GetRequiredService<PoolConfig>().HealthFailureThreshold));

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
            new Wk600Drive(sp.GetRequiredService<PoolConfig>(),
                sp.GetRequiredService<ILogger<Wk600Drive>>(), sp.GetRequiredService<EquipmentHealth>()));

        // Logique pure
        services.AddSingleton(sp => new FiltrationManager(sp.GetRequiredService<PoolConfig>()));
        services.AddSingleton(sp => new PhPidController(sp.GetRequiredService<PoolConfig>()));

        // PumpPrimingService — singleton partagé, verrou anti-double-déclenchement
        services.AddSingleton(sp => new PumpPrimingService(
            sp.GetRequiredService<EzoPmp>(),
            sp.GetRequiredService<ILogger<PumpPrimingService>>()));

        // MqttService — singleton pour que DriveService/SensorService le résolvent
        services.AddSingleton(sp => new MqttService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<FiltrationManager>(),
            sp.GetRequiredService<PhPidController>(),
            sp.GetRequiredService<EzoPh>(),
            sp.GetRequiredService<EzoOrp>(),
            sp.GetRequiredService<Wk600Drive>(),
            sp.GetRequiredService<PumpPrimingService>(),
            sp.GetRequiredService<FileLoggerProvider>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<ILogger<MqttService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<MqttService>());

        // Autres hosted services — factory lambdas (AOT-safe, pas de reflection)
        services.AddHostedService(sp => new ElectrolyzerService(
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<Pcf8574>(),
            sp.GetRequiredService<MqttService>(),
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<ILogger<ElectrolyzerService>>()));
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
            sp.GetRequiredService<MqttService>(),
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
        services.AddHostedService(sp => new HealthService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<EquipmentHealth>(),
            sp.GetRequiredService<MqttService>(),
            sp.GetRequiredService<ILogger<HealthService>>()));
        services.AddHostedService(sp => new RescueService(
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<PoolState>(),
            sp.GetRequiredService<Pcf8574>(),
            sp.GetRequiredService<Wk600Drive>(),
            sp.GetRequiredService<MqttService>(),
            sp.GetRequiredService<ILogger<RescueService>>()));
    })
    .Build();

// ── Log de version au démarrage ───────────────────────────────────────────────
// La version est lue depuis l'attribut AssemblyInformationalVersion injecté
// par le workflow GitHub Actions publish.yml via /p:InformationalVersion=x.x.x.x.
// Elle apparaît dans les logs dès le démarrage pour faciliter le diagnostic.
var startLogger = host.Services.GetRequiredService<ILogger<Program>>();
var version = Assembly
    .GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion ?? "inconnue";
startLogger.LogInformation(
    "╔══════════════════════════════════════════════════╗");
startLogger.LogInformation(
    "║  PiscineController v{Version}", version);
startLogger.LogInformation(
    "║  Démarrage : {Now:yyyy-MM-dd HH:mm:ss}",
    DateTime.Now);
startLogger.LogInformation(
    "╚══════════════════════════════════════════════════╝");

await host.RunAsync();
