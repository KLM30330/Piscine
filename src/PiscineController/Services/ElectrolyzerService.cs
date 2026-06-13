using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Hardware;

namespace PiscineController.Services;

public sealed class ElectrolyzerService : BackgroundService
{
    private readonly PoolState _state;
    private readonly Pcf8574 _relay;
    private readonly ILogger<ElectrolyzerService> _logger;
    private bool _electrolyzerOn;

    public ElectrolyzerService(PoolState state,
        Pcf8574 relay, ILogger<ElectrolyzerService> logger)
    {
        _state = state; _relay = relay; _logger = logger;
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
        try { _relay.SetPin(0, false); } catch { }
    }

    private void Apply()
    {
        bool active = _electrolyzerOn && _state.PumpRunning;
        _relay.SetPin(0, active);
    }
}
