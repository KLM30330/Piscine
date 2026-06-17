using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Hardware;

namespace PiscineController.Services;

public sealed record DisplayCommand(string Line1, string Line2, int DurationMs);

public sealed class DisplayService : BackgroundService
{
    private readonly Lcd1602 _lcd;
    private readonly PoolConfig _cfg;
    private readonly ILogger<DisplayService> _logger;
    private readonly Channel<DisplayCommand> _channel =
        Channel.CreateBounded<DisplayCommand>(new BoundedChannelOptions(4)
        { FullMode = BoundedChannelFullMode.DropOldest });

    public DisplayService(Lcd1602 lcd, PoolConfig cfg, ILogger<DisplayService> logger)
    {
        _lcd = lcd; _cfg = cfg; _logger = logger;
    }

    // durationMs = null → utilise LcdBacklightTimeout (config) comme durée
    // par défaut, au lieu d'une constante arbitraire non reliée à la config.
    public void Show(string line1, string line2, int? durationMs = null) =>
        _channel.Writer.TryWrite(new DisplayCommand(
            line1, line2, durationMs ?? _cfg.LcdBacklightTimeout * 1000));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _lcd.Initialize();
        // Écran éteint au démarrage : Initialize() coupe déjà le
        // rétroéclairage, mais on le redit explicitement ici pour que ce
        // comportement reste visible et garanti même si Lcd1602 change.
        _lcd.SetBacklight(false);
        _logger.LogInformation("DisplayService: LCD initialisé, écran éteint");

        await foreach (var cmd in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                _lcd.Clear();
                _lcd.PrintLine(0, cmd.Line1);
                _lcd.PrintLine(1, cmd.Line2);
                _lcd.SetBacklight(true);
                await Task.Delay(cmd.DurationMs, ct);
                _lcd.SetBacklight(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "DisplayService: erreur LCD"); }
        }
    }
}
