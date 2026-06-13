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
