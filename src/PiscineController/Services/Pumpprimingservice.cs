using Microsoft.Extensions.Logging;
using PiscineController.Hardware;

namespace PiscineController.Services;

// Logique d'amorçage de la pompe doseuse (chrono + dosage), partagée entre
// ButtonService (bouton physique) et MqttService (commande HA), avec un seul
// verrou anti-double-déclenchement commun aux deux déclencheurs : sans ce
// partage, un appui bouton et une commande MQTT simultanés pourraient lancer
// deux dosages en parallèle.
public sealed class PumpPrimingService
{
    private readonly EzoPmp _pmp;
    private readonly DisplayService _display;
    private readonly ILogger<PumpPrimingService> _logger;
    private int _priming;

    // Notifie le début/fin réel de l'amorçage (ex. pour publier un état sur
    // MQTT) — l'amorçage étant asynchrone (chrono de plusieurs secondes), un
    // simple avant/après autour de TryPrime() ne suffirait pas comme pour les
    // commandes instantanées (étalonnage, reset défaut).
    public event Action<bool>? StateChanged;

    public PumpPrimingService(EzoPmp pmp, DisplayService display, ILogger<PumpPrimingService> logger)
    {
        _pmp = pmp; _display = display; _logger = logger;
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

    // Affiche un chrono pendant toute la durée réelle du dosage (au lieu
    // d'un texte statique), conformément au README : "affichage LCD
    // 'amorçage ph- ' + chrono".
    private async Task RunAsync(double volumeMl)
    {
        try
        {
            int totalMs = EzoPmp.EstimateDoseMs(volumeMl);
            int totalSeconds = Math.Max(1, (int)Math.Ceiling(totalMs / 1000.0));

            var doseTask = Task.Run(() => _pmp.Dose(volumeMl));

            for (int elapsed = 1; elapsed <= totalSeconds; elapsed++)
            {
                _display.Show("Amorcage ph-", $"{elapsed}/{totalSeconds}s...", 1100);
                await Task.Delay(1000);
            }

            await doseTask;
            _display.Show("Amorcage ph-", "Termine", 2000);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Amorçage pompe: échec du dosage {Vol} mL", volumeMl);
            _display.Show("Amorcage ph-", "ERREUR", 2000);
        }
        finally
        {
            Interlocked.Exchange(ref _priming, 0);
            StateChanged?.Invoke(false);
        }
    }
}
