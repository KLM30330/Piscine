using Microsoft.Extensions.Logging;
using PiscineController.Hardware;

namespace PiscineController.Services;

// Logique d'amorçage de la pompe doseuse (chrono + dosage) partagée entre
// MqttService (commande HA) avec un seul verrou anti-double-déclenchement.
public sealed class PumpPrimingService
{
    private readonly EzoPmp _pmp;
    private readonly ILogger<PumpPrimingService> _logger;
    private int _priming;

    // Notifie le début/fin réel de l'amorçage pour publication MQTT.
    public event Action<bool>? StateChanged;

    public PumpPrimingService(EzoPmp pmp, ILogger<PumpPrimingService> logger)
    {
        _pmp = pmp; _logger = logger;
    }

    // Retourne false si un amorçage est déjà en cours (appel ignoré).
    public bool TryPrime(double volumeMl)
    {
        if (Interlocked.CompareExchange(ref _priming, 1, 0) != 0)
        {
            _logger.LogDebug("Amorçage déjà en cours, demande ignorée");
            return false;
        }
        _logger.LogInformation("Amorçage pompe doseuse {Vol} mL", volumeMl);
        StateChanged?.Invoke(true);
        _ = RunAsync(volumeMl);
        return true;
    }

    private async Task RunAsync(double volumeMl)
    {
        try
        {
            int totalMs = EzoPmp.EstimateDoseMs(volumeMl);
            _logger.LogInformation("Amorçage en cours: {Vol} mL, durée estimée {Ms} ms", volumeMl, totalMs);
            await Task.Run(() => _pmp.Dose(volumeMl));
            _logger.LogInformation("Amorçage terminé");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Amorçage pompe: échec du dosage {Vol} mL", volumeMl);
        }
        finally
        {
            Interlocked.Exchange(ref _priming, 0);
            StateChanged?.Invoke(false);
        }
    }
}
