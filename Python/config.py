# config.py  — Configuration centrale du système piscine
"""
Tous les paramètres modifiables sont ici.
Ne toucher aux autres fichiers que pour corriger un bug.
"""

# ─────────────────────────────────────────────────────────────────────────────
# MQTT
# ─────────────────────────────────────────────────────────────────────────────
MQTT_BROKER    = "192.168.1.XX"      # ← IP du Raspberry Home Assistant
MQTT_PORT      = 1883
MQTT_USER      = "pool_user"         # ← login MQTT (Mosquitto)
MQTT_PASSWORD  = "VotreMotDePasse"   # ← mot de passe MQTT
MQTT_CLIENT_ID = "pool_controller"
MQTT_PREFIX    = "pool"              # topic racine : pool/...
MQTT_HA_DISC   = "homeassistant"     # préfixe autodiscovery HA

# Device info Home Assistant
HA_DEVICE_NAME  = "Piscine"
HA_DEVICE_ID    = "pool_rpi"
HA_DEVICE_MODEL = "RPi3B+ / Atlas Scientific / WK600-D"
HA_DEVICE_MFR   = "DIY"

# ─────────────────────────────────────────────────────────────────────────────
# BUS I2C
# ─────────────────────────────────────────────────────────────────────────────
I2C_BUS = 1

# Atlas Scientific — adresses I2C par défaut
ATLAS_PH_ADDR   = 0x63
ATLAS_ORP_ADDR  = 0x62
ATLAS_RTD_ADDR  = 0x66
ATLAS_PMP_ADDR  = 0x67

# PCF8574 — relais électrolyseur
PCF8574_ADDR        = 0x20   # jumpers A0=A1=A2=0
PCF_RELAY_ELECTRO   = 0      # broche P0 → relais électrolyseur
# Logique ACTIF HAUT : écrire 1 → relais ON

# LCD1602 via I2C (PCF8574 dédié écran, adresse par défaut 0x27)
LCD_I2C_ADDR    = 0x27
LCD_COLS        = 16
LCD_ROWS        = 2

# ─────────────────────────────────────────────────────────────────────────────
# BUS 1-WIRE  (GPIO4 — dtoverlay=w1-gpio,gpiopin=4 dans /boot/config.txt)
# ─────────────────────────────────────────────────────────────────────────────
ONEWIRE_PUMP_SENSOR_ID = None   # None = autodétection | ex. "28-0123456789ab"
PUMP_TEMP_ALERT_C      = 60.0
PUMP_TEMP_CRITICAL_C   = 70.0

# ─────────────────────────────────────────────────────────────────────────────
# BOUTONS POUSSOIR (GPIO BCM)
# ─────────────────────────────────────────────────────────────────────────────
BTN_LCD_DISPLAY  = 5    # Affichage LCD 30 s (T°, pH, ORP)
BTN_PRIME_PUMP   = 6    # Amorçage pompe doseuse
BTN_PAUSE_FILTER = 13   # Pause filtration
BTN_RESUME_FILTER= 19   # Reprise filtration auto

# ─────────────────────────────────────────────────────────────────────────────
# VARIATEUR WK600-D  (Modbus RTU via USB/RS485)
# ─────────────────────────────────────────────────────────────────────────────
MODBUS_PORT      = "/dev/ttyUSB0"
MODBUS_SLAVE_ID  = 1
MODBUS_BAUDRATE  = 9600
MODBUS_PARITY    = "N"
MODBUS_STOPBITS  = 1

# Limites moteur monophasé + condensateur
FREQ_MIN_ABSOLUTE  = 30.0   # Hz — jamais en dessous (condensateur)
FREQ_MIN_FILTRATION= 35.0   # Hz — minimum filtration normale
FREQ_START_MIN     = 40.0   # Hz — démarrage minimum
FREQ_NOMINAL       = 50.0   # Hz — nominal
FREQ_RAMP_STEP     = 2.0    # Hz par pas de rampe
FREQ_RAMP_DELAY    = 0.5    # s entre chaque pas

# ─────────────────────────────────────────────────────────────────────────────
# PISCINE — HYDRAULIQUE
# ─────────────────────────────────────────────────────────────────────────────
POOL_VOLUME_M3   = 38.0    # m³
PUMP_FLOW_M3H    = 17.0    # m³/h à 50 Hz (débit nominal)

# Planning filtration : créneaux horaires préférés (h_début, h_fin)
FILTRATION_SLOTS = [
    (8,  13),    # matin
    (14, 21),    # après-midi / soirée
]

# ─────────────────────────────────────────────────────────────────────────────
# RÉGULATION ORP → vitesse pompe
# ─────────────────────────────────────────────────────────────────────────────
ORP_CRITICAL_LOW   = 550.0   # mV — eau dangereuse
ORP_LOW            = 620.0   # mV
ORP_TARGET_LOW     = 650.0   # mV — cible basse
ORP_TARGET_HIGH    = 750.0   # mV — cible haute
ORP_HIGH           = 800.0   # mV
ORP_DEADBAND       = 10.0    # mV — zone morte
ORP_STABILITY_N    = 3       # mesures consécutives pour confirmer un état
ORP_MIN_FREQ_CHANGE= 5.0     # Hz — variation minimale avant action
ORP_MIN_CHANGE_INTERVAL = 120.0  # s entre deux changements de fréquence

# ─────────────────────────────────────────────────────────────────────────────
# RÉGULATION pH
# ─────────────────────────────────────────────────────────────────────────────
PH_TARGET          = 7.2
PH_DEADBAND        = 0.05
PH_KP              = 10.0
PH_KI              = 0.1
PH_KD              = 1.0
PH_DOSE_MIN_ML     = 1.0
PH_DOSE_MAX_ML     = 50.0
PH_MIN_DELAY_S     = 600      # 10 min entre deux doses
DOSE_ONLY_WHEN_PUMP_RUNNING = True

PH_ALERT_LOW       = 6.8
PH_ALERT_HIGH      = 7.6

# Amorçage pompe doseuse
PRIME_VOLUME_ML    = 20.0    # mL à doser lors de l'amorçage

# ─────────────────────────────────────────────────────────────────────────────
# INTERVALLES DE BOUCLE (secondes)
# ─────────────────────────────────────────────────────────────────────────────
INTERVAL_SENSORS   = 60     # lecture capteurs Atlas + régulation
INTERVAL_DRIVE     = 30     # télémétrie variateur
INTERVAL_PUMP_TEMP = 30     # lecture sonde 1-Wire
LOOP_SLEEP         = 5      # granularité boucle principale

# ─────────────────────────────────────────────────────────────────────────────
# MODES DE FONCTIONNEMENT
# ─────────────────────────────────────────────────────────────────────────────
# AUTO   : planning calculé selon T° eau + ORP
# FORCED : pompe ON en permanence à freq configurée
# BOOST  : pompe ON à 50 Hz jusqu'à ORP cible atteint
# PAUSE  : pompe OFF (bouton ou HA), reprend à la prochaine plage auto
# STOP   : pompe OFF indéfiniment (commande HA)
MODES = ["auto", "forced", "boost", "pause", "stop"]
DEFAULT_MODE       = "auto"
BOOST_FREQ         = 50.0    # Hz en mode boost
BOOST_ORP_TARGET   = 700.0   # mV — ORP cible pour sortir du boost automatiquement

# ─────────────────────────────────────────────────────────────────────────────
# LCD — durées d'affichage (secondes)
# ─────────────────────────────────────────────────────────────────────────────
LCD_DISPLAY_DURATION  = 30   # affichage mesures (bouton 1)
LCD_PRIME_MAX_S       = 120  # durée max amorçage (bouton 2)
LCD_PAUSE_DURATION    = 30   # affichage pause (bouton 3)
LCD_RESUME_DURATION   = 30   # affichage reprise (bouton 4)
LCD_BACKLIGHT_TIMEOUT = 60   # extinction rétroéclairage après N secondes d'inactivité
