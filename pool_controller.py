# pool_controller.py — Orchestrateur principal système piscine
"""
Raspberry Pi 3B+ — Contrôleur piscine autonome

Matériel :
  I2C   : EZO-pH, EZO-ORP, EZO-RTD, EZO-PMP, PCF8574 (relais + LCD)
  1-Wire: DS18B20 (température pompe, GPIO4)
  GPIO  : 4 boutons poussoir (GPIO 5,6,13,19)
  USB   : Variateur WK600-D via RS485/Modbus RTU

Fonctions :
  - Régulation automatique pH (PID → pompe doseuse)
  - Filtration automatique (planning T°eau/2 + correction ORP)
  - Modes : auto / forced / boost / pause / stop
  - Interface LCD + boutons poussoir
  - Télémétrie complète vers Home Assistant via MQTT
  - Autodiscovery HA (entités créées automatiquement)
"""

import time, json, logging, signal, sys, threading
import paho.mqtt.client as mqtt

import config as C
from atlas_sensors  import PHSensor, ORPSensor, TempSensor, PeristalticPump, AtlasSensorError
from pcf8574        import PCF8574Relay, ElectrolyzerController, PCF8574Error
from lcd_display    import LCD1602, PoolDisplay, LCDError
from vfd_drive      import WK600Drive, VFDError
from onewire_sensors import PumpTempMonitor, DS18B20Error
from gpio_buttons   import ButtonManager, ButtonError
from filtration     import FiltrationManager, FilterMode
from ph_controller  import PHController
from ha_discovery   import all_entities

# ─────────────────────────────────────────────────────────────────────────────
# Logging
# ─────────────────────────────────────────────────────────────────────────────
logging.basicConfig(
    level   = logging.INFO,
    format  = "%(asctime)s [%(name)-14s] %(levelname)-7s %(message)s",
    handlers= [
        logging.StreamHandler(sys.stdout),
        logging.FileHandler("/var/log/pool_controller.log"),
    ]
)
logger = logging.getLogger("pool")


# ─────────────────────────────────────────────────────────────────────────────
# Contrôleur principal
# ─────────────────────────────────────────────────────────────────────────────

class PoolController:

    def __init__(self):
        self._running = True
        signal.signal(signal.SIGTERM, self._sig_handler)
        signal.signal(signal.SIGINT,  self._sig_handler)

        # ── Timers de boucle ──────────────────────────────────
        self._t_sensors    = 0.0
        self._t_drive      = 0.0
        self._t_pump_temp  = 0.0

        # ── Cache dernières mesures ───────────────────────────
        self._last_temp    = 25.0
        self._last_ph      = 7.2
        self._last_orp     = 650.0
        self._last_orp_result = {}

        # ── État commandes HA ─────────────────────────────────
        self._prime_active = False

        logger.info("=" * 55)
        logger.info("  Démarrage contrôleur piscine")
        logger.info("=" * 55)

        self._init_hardware()
        self._init_mqtt()
        self._init_buttons()

    # ═══════════════════════════════════════════════════════════
    # Initialisation matériel
    # ═══════════════════════════════════════════════════════════

    def _init_hardware(self):
        logger.info("[INIT] Matériel...")

        # LCD (avant tout pour afficher le boot)
        try:
            lcd = LCD1602(address=C.LCD_I2C_ADDR, bus=C.I2C_BUS)
            self.display = PoolDisplay(lcd)
            self.display.show_boot()
            logger.info("[INIT] LCD OK")
        except LCDError as e:
            logger.warning(f"[INIT] LCD non disponible : {e}")
            self.display = None

        # Capteurs Atlas Scientific
        try:
            self.ph_sensor   = PHSensor(bus=C.I2C_BUS, address=C.ATLAS_PH_ADDR)
            self.orp_sensor  = ORPSensor(bus=C.I2C_BUS, address=C.ATLAS_ORP_ADDR)
            self.temp_sensor = TempSensor(bus=C.I2C_BUS, address=C.ATLAS_RTD_ADDR)
            self.peri_pump   = PeristalticPump(bus=C.I2C_BUS, address=C.ATLAS_PMP_ADDR)
            logger.info("[INIT] Capteurs Atlas OK")
        except AtlasSensorError as e:
            logger.critical(f"[INIT] Capteurs Atlas : {e}")
            sys.exit(1)

        # PCF8574 + Électrolyseur
        try:
            self.pcf    = PCF8574Relay(address=C.PCF8574_ADDR, bus=C.I2C_BUS)
            self.electro = ElectrolyzerController(self.pcf, pin=C.PCF_RELAY_ELECTRO)
            logger.info("[INIT] PCF8574 / Électrolyseur OK")
        except PCF8574Error as e:
            logger.critical(f"[INIT] PCF8574 : {e}")
            sys.exit(1)

        # Variateur WK600-D
        try:
            self.drive = WK600Drive(
                port      = C.MODBUS_PORT,
                slave_id  = C.MODBUS_SLAVE_ID,
                baudrate  = C.MODBUS_BAUDRATE,
                parity    = C.MODBUS_PARITY,
                stopbits  = C.MODBUS_STOPBITS,
                freq_min_abs = C.FREQ_MIN_ABSOLUTE,
                freq_start   = C.FREQ_START_MIN,
                freq_nominal = C.FREQ_NOMINAL,
                ramp_step    = C.FREQ_RAMP_STEP,
                ramp_delay   = C.FREQ_RAMP_DELAY,
            )
            logger.info("[INIT] Variateur WK600-D OK")
        except VFDError as e:
            logger.critical(f"[INIT] Variateur : {e}")
            sys.exit(1)

        # Gestionnaire filtration
        self.filtration = FiltrationManager(drive=self.drive)

        # PID pH
        self.ph_pid = PHController(
            target    = C.PH_TARGET,
            deadband  = C.PH_DEADBAND,
            kp        = C.PH_KP, ki=C.PH_KI, kd=C.PH_KD,
            dose_min  = C.PH_DOSE_MIN_ML,
            dose_max  = C.PH_DOSE_MAX_ML,
            min_delay_s = C.PH_MIN_DELAY_S,
        )

        # Sonde température pompe (1-Wire)
        try:
            self.pump_temp = PumpTempMonitor(
                sensor_id  = C.ONEWIRE_PUMP_SENSOR_ID,
                alert_c    = C.PUMP_TEMP_ALERT_C,
                critical_c = C.PUMP_TEMP_CRITICAL_C,
            )
            logger.info(f"[INIT] DS18B20 OK : {self.pump_temp.sensor_id}")
        except DS18B20Error as e:
            logger.critical(f"[INIT] DS18B20 : {e}")
            sys.exit(1)

        logger.info("[INIT] Tout le matériel initialisé")

    # ═══════════════════════════════════════════════════════════
    # Boutons poussoir
    # ═══════════════════════════════════════════════════════════

    def _init_buttons(self):
        try:
            self.buttons = ButtonManager({
                "lcd"   : C.BTN_LCD_DISPLAY,
                "prime" : C.BTN_PRIME_PUMP,
                "pause" : C.BTN_PAUSE_FILTER,
                "resume": C.BTN_RESUME_FILTER,
            })
            self.buttons.on("lcd",    self._btn_lcd)
            self.buttons.on("prime",  self._btn_prime)
            self.buttons.on("pause",  self._btn_pause)
            self.buttons.on("resume", self._btn_resume)
            logger.info("[INIT] Boutons GPIO OK")
        except ButtonError as e:
            logger.warning(f"[INIT] Boutons GPIO : {e}")
            self.buttons = None

    def _btn_lcd(self):
        """Bouton 1 — Affichage LCD 30 s (T°, pH, ORP)."""
        if self.display:
            self.display.show_measures(
                self._last_temp, self._last_ph, self._last_orp,
                duration=C.LCD_DISPLAY_DURATION,
            )

    def _btn_prime(self):
        """Bouton 2 — Amorçage pompe doseuse."""
        if self._prime_active:
            logger.info("[BTN] Amorçage déjà en cours")
            return
        self._prime_active = True
        if self.display:
            self.display.start_prime(max_s=C.LCD_PRIME_MAX_S)

        def _do_prime():
            logger.info(f"[BTN] Amorçage {C.PRIME_VOLUME_ML} mL")
            ok = self.peri_pump.dose(C.PRIME_VOLUME_ML)
            self.pub(f"{C.MQTT_PREFIX}/ph/dispensing", "true" if ok else "false")
            # Attente fin de dosage
            for _ in range(C.LCD_PRIME_MAX_S):
                if not self._prime_active: break
                if self.display: self.display.update_prime_chrono()
                if not self.peri_pump.is_dispensing():
                    break
                time.sleep(1)
            self.peri_pump.stop()
            self._prime_active = False
            if self.display: self.display.stop_prime()
            self.pub(f"{C.MQTT_PREFIX}/ph/dispensing", "false")
            logger.info("[BTN] Amorçage terminé")

        threading.Thread(target=_do_prime, daemon=True).start()

    def _btn_pause(self):
        """Bouton 3 — Pause filtration."""
        self.filtration.pause()
        if self.display:
            self.display.show_pause(C.LCD_PAUSE_DURATION)
        self.pub(f"{C.MQTT_PREFIX}/filtration/mode", "pause")
        self.pub(f"{C.MQTT_PREFIX}/cmd/mode/state",  "pause")
        logger.info("[BTN] Filtration en pause")

    def _btn_resume(self):
        """Bouton 4 — Reprise filtration auto."""
        self.filtration.resume_auto()
        if self.display:
            self.display.show_resume(C.LCD_RESUME_DURATION)
        self.pub(f"{C.MQTT_PREFIX}/filtration/mode", "auto")
        self.pub(f"{C.MQTT_PREFIX}/cmd/mode/state",  "auto")
        logger.info("[BTN] Reprise filtration auto")

    # ═══════════════════════════════════════════════════════════
    # MQTT
    # ═══════════════════════════════════════════════════════════

    def _init_mqtt(self):
        logger.info(f"[MQTT] Connexion {C.MQTT_BROKER}:{C.MQTT_PORT} user={C.MQTT_USER}")
        self.mqtt = mqtt.Client(client_id=C.MQTT_CLIENT_ID, clean_session=True)
        self.mqtt.username_pw_set(C.MQTT_USER, C.MQTT_PASSWORD)
        self.mqtt.will_set(f"{C.MQTT_PREFIX}/status", "offline", qos=1, retain=True)
        self.mqtt.on_connect    = self._on_connect
        self.mqtt.on_disconnect = self._on_disconnect
        self.mqtt.on_message    = self._on_message
        try:
            self.mqtt.connect(C.MQTT_BROKER, C.MQTT_PORT, keepalive=60)
            self.mqtt.loop_start()
        except Exception as e:
            logger.error(f"[MQTT] Connexion : {e}")

    def _on_connect(self, client, ud, flags, rc):
        if rc != 0:
            logger.error(f"[MQTT] rc={rc}")
            return
        logger.info("[MQTT] Connecté")
        client.publish(f"{C.MQTT_PREFIX}/status", "online", qos=1, retain=True)

        # Abonnements commandes
        subs = [
            f"{C.MQTT_PREFIX}/cmd/mode/set",
            f"{C.MQTT_PREFIX}/cmd/freq/set",
            f"{C.MQTT_PREFIX}/cmd/ph_target/set",
            f"{C.MQTT_PREFIX}/cmd/orp_low/set",
            f"{C.MQTT_PREFIX}/cmd/orp_high/set",
            f"{C.MQTT_PREFIX}/cmd/electrolyzer/set",
            f"{C.MQTT_PREFIX}/cmd/prime",
            f"{C.MQTT_PREFIX}/cmd/calibrate_ph/set",
        ]
        for t in subs:
            client.subscribe(t, qos=1)

        # Autodiscovery HA
        for topic, payload in all_entities():
            client.publish(topic, json.dumps(payload), qos=1, retain=True)
        logger.info(f"[MQTT] Autodiscovery : {len(all_entities())} entités")

        # États initiaux
        self._publish_initial_states()

    def _on_disconnect(self, client, ud, rc):
        if rc != 0:
            logger.warning(f"[MQTT] Déconnexion inattendue rc={rc}")

    def _on_message(self, client, ud, msg):
        topic   = msg.topic
        payload = msg.payload.decode("utf-8").strip()
        logger.info(f"[MQTT] ← {topic} = {payload!r}")
        pfx = C.MQTT_PREFIX

        try:
            # ── Mode filtration ──────────────────────────────
            if topic == f"{pfx}/cmd/mode/set":
                freq = None
                if ":" in payload:
                    mode_str, fstr = payload.split(":", 1)
                    freq = float(fstr)
                else:
                    mode_str = payload
                self.filtration.set_mode(mode_str.strip(), freq)
                self.pub(f"{pfx}/cmd/mode/state",   self.filtration.mode.value)
                self.pub(f"{pfx}/filtration/mode",  self.filtration.mode.value)
                if self.display:
                    self.display.show_mode(
                        self.filtration.mode.value,
                        self.drive.current_freq if self.drive.is_running else 0,
                    )

            # ── Fréquence forcée ─────────────────────────────
            elif topic == f"{pfx}/cmd/freq/set":
                hz = float(payload)
                self.filtration.set_mode("forced", freq_hz=hz)
                self.pub(f"{pfx}/cmd/freq/state",  hz)
                self.pub(f"{pfx}/cmd/mode/state",  "forced")
                self.pub(f"{pfx}/filtration/mode", "forced")

            # ── Consigne pH ──────────────────────────────────
            elif topic == f"{pfx}/cmd/ph_target/set":
                self.ph_pid.target = float(payload)
                self.pub(f"{pfx}/cmd/ph_target/state", payload)
                logger.info(f"[CMD] pH cible → {payload}")

            # ── Seuils ORP ───────────────────────────────────
            elif topic == f"{pfx}/cmd/orp_low/set":
                C.ORP_TARGET_LOW = float(payload)
                self.pub(f"{pfx}/cmd/orp_low/state", payload)
            elif topic == f"{pfx}/cmd/orp_high/set":
                C.ORP_TARGET_HIGH = float(payload)
                self.pub(f"{pfx}/cmd/orp_high/state", payload)

            # ── Électrolyseur ─────────────────────────────────
            elif topic == f"{pfx}/cmd/electrolyzer/set":
                if payload == "ON":
                    self.electro.enable()
                else:
                    self.electro.disable()
                self.pub(f"{pfx}/cmd/electrolyzer/state", payload)

            # ── Amorçage pompe doseuse (depuis HA) ───────────
            elif topic == f"{pfx}/cmd/prime":
                threading.Thread(target=self._btn_prime, daemon=True).start()

            # ── Calibration pH ────────────────────────────────
            elif topic == f"{pfx}/cmd/calibrate_ph/set":
                self._do_calibrate_ph(payload)
                self.pub(f"{pfx}/cmd/calibrate_ph/state", payload)

        except Exception as e:
            logger.error(f"[CMD] Erreur traitement {topic} : {e}")

    def _do_calibrate_ph(self, step: str):
        """Exécute une étape de calibration pH."""
        logger.info(f"[CAL] Calibration pH : {step}")
        if   step == "mid_7"  : self.ph_sensor.calibrate_mid(7.00)
        elif step == "low_4"  : self.ph_sensor.calibrate_low(4.00)
        elif step == "high_10": self.ph_sensor.calibrate_high(10.00)
        elif step == "clear"  : self.ph_sensor.clear_cal()
        pts = self.ph_sensor.get_cal_points()
        self.pub(f"{C.MQTT_PREFIX}/alarm/message",
                 f"Calibration pH {step} effectuée ({pts} pts)")
        logger.info(f"[CAL] pH calibration {step} OK — {pts} points")

    def pub(self, topic: str, value, retain: bool = True):
        """Publication MQTT avec gestion d'erreur."""
        try:
            if isinstance(value, (dict, list)):
                payload = json.dumps(value)
            else:
                payload = str(value)
            self.mqtt.publish(topic, payload, qos=1, retain=retain)
        except Exception as e:
            logger.warning(f"[MQTT] Pub {topic} : {e}")

    def _publish_initial_states(self):
        pfx = C.MQTT_PREFIX
        self.pub(f"{pfx}/cmd/mode/state",           C.DEFAULT_MODE)
        self.pub(f"{pfx}/cmd/freq/state",           C.FREQ_NOMINAL)
        self.pub(f"{pfx}/cmd/ph_target/state",      C.PH_TARGET)
        self.pub(f"{pfx}/cmd/orp_low/state",        C.ORP_TARGET_LOW)
        self.pub(f"{pfx}/cmd/orp_high/state",       C.ORP_TARGET_HIGH)
        self.pub(f"{pfx}/cmd/electrolyzer/state",   "OFF")
        self.pub(f"{pfx}/filtration/mode",          C.DEFAULT_MODE)
        self.pub(f"{pfx}/pump/running",             "false")
        self.pub(f"{pfx}/alarm/active",             "false")
        self.pub(f"{pfx}/pump_temp/state",          "unknown")
        self.pub(f"{pfx}/electrolyzer/is_on",       "false")
        self.pub(f"{pfx}/pump_temp/sensor_id",
                 self.pump_temp.sensor_id if self.pump_temp else "—")

    # ═══════════════════════════════════════════════════════════
    # Cycles de mesure
    # ═══════════════════════════════════════════════════════════

    def _cycle_sensors(self):
        """Lecture Atlas Scientific + régulation pH + ORP."""
        pfx = C.MQTT_PREFIX
        try:
            temp = self.temp_sensor.read()
            ph   = self.ph_sensor.read(temp_c=temp)
            orp  = self.orp_sensor.read()
            self._last_temp = temp
            self._last_ph   = ph
            self._last_orp  = orp
        except AtlasSensorError as e:
            logger.error(f"[SENSORS] {e}")
            return

        # ORP → fréquence pompe + alarmes
        orp_result = self.filtration.update_orp(orp)
        self._last_orp_result = orp_result
        self.filtration.check_boost_exit(orp)

        # pH → dosage PID
        self._regulate_ph(ph)

        # Publication capteurs eau
        self.pub(f"{pfx}/sensor/water_temp_c", round(temp, 1))
        self.pub(f"{pfx}/sensor/ph",           round(ph,   2))
        self.pub(f"{pfx}/sensor/orp_mv",       round(orp,  0))
        self.pub(f"{pfx}/sensor/water_state",  orp_result["water_state"])
        self.pub(f"{pfx}/filtration/mode",     self.filtration.mode.value)

        # Infos planning
        sched = self.filtration.schedule_info(temp)
        self.pub(f"{pfx}/filtration/required_hours", sched["required_hours"])
        self.pub(f"{pfx}/filtration/slots",          sched["slots"])

        # Pompe
        self.pub(f"{pfx}/pump/running",        str(self.drive.is_running).lower())
        self.pub(f"{pfx}/pump/freq_hz",        round(self.drive.current_freq, 1))

        # Alarmes ORP
        if orp_result.get("orp_alarm"):
            for msg in orp_result["alarms"].values():
                self.pub(f"{pfx}/alarm/active",  "true")
                self.pub(f"{pfx}/alarm/message", msg)

        logger.info(
            f"T={temp:.1f}°C pH={ph:.2f} ORP={orp:.0f}mV "
            f"état={orp_result['water_state']} "
            f"pompe={'ON' if self.drive.is_running else 'OFF'} "
            f"{self.drive.current_freq:.0f}Hz "
            f"mode={self.filtration.mode.value}"
        )

    def _regulate_ph(self, ph: float):
        """Calcule et applique le dosage pH- si nécessaire."""
        pfx = C.MQTT_PREFIX
        if C.DOSE_ONLY_WHEN_PUMP_RUNNING and not self.drive.is_running:
            return
        dose = self.ph_pid.compute_dose(ph)
        if dose > 0:
            ok = self.peri_pump.dose(dose)
            if ok:
                self.ph_pid.record_dose(dose)
                self.pub(f"{pfx}/ph/dose_last_ml",  round(dose, 2))
                self.pub(f"{pfx}/ph/dose_total_ml", round(self.ph_pid.total_ml, 2))
                self.pub(f"{pfx}/ph/dose_count",    self.ph_pid.dose_count)
                self.pub(f"{pfx}/ph/dispensing",    "true")
                threading.Timer(
                    30, lambda: self.pub(f"{pfx}/ph/dispensing", "false")
                ).start()

        # Alarmes pH
        if ph < C.PH_ALERT_LOW:
            self.pub(f"{pfx}/alarm/active",  "true")
            self.pub(f"{pfx}/alarm/message", f"pH bas : {ph:.2f}")
        elif ph > C.PH_ALERT_HIGH:
            self.pub(f"{pfx}/alarm/active",  "true")
            self.pub(f"{pfx}/alarm/message", f"pH élevé : {ph:.2f}")

    def _cycle_drive(self):
        """Lecture télémétrie variateur."""
        pfx = C.MQTT_PREFIX
        try:
            s = self.drive.read_status()
            d = s.to_mqtt()
            self.pub(f"{pfx}/drive/out_current_a", d["out_current_a"])
            self.pub(f"{pfx}/drive/out_voltage_v", d["out_voltage_v"])
            self.pub(f"{pfx}/drive/out_power_kw",  d["out_power_kw"])
            self.pub(f"{pfx}/drive/dc_bus_v",      d["dc_bus_v"])
            self.pub(f"{pfx}/drive/drive_temp_c",  d["drive_temp_c"])
            self.pub(f"{pfx}/drive/run_time_h",    d["run_time_h"])
            self.pub(f"{pfx}/drive/motor_rpm",     d["motor_rpm"])
            self.pub(f"{pfx}/drive/in_freq_hz",    d["in_freq_hz"])
            self.pub(f"{pfx}/drive/in_voltage_v",  d["in_voltage_v"])
            self.pub(f"{pfx}/drive/fault_code",    d["fault_code"])
            self.pub(f"{pfx}/drive/fault_label",   d["fault_label"])
            self.pub(f"{pfx}/drive/is_fault",      str(d["is_fault"]).lower())
            self.pub(f"{pfx}/drive/dc_bus_v",      d["dc_bus_v"])
            self.pub(f"{pfx}/pump/running",        str(s.is_running).lower())
            self.pub(f"{pfx}/pump/freq_hz",        s.out_freq_hz)
            if s.is_fault:
                self.pub(f"{pfx}/alarm/active",  "true")
                self.pub(f"{pfx}/alarm/message", f"Défaut variateur : {s.fault_label}")
        except Exception as e:
            logger.error(f"[DRIVE] Télémétrie : {e}")

    def _cycle_pump_temp(self):
        """Lecture sonde DS18B20 température pompe."""
        pfx = C.MQTT_PREFIX
        try:
            r = self.pump_temp.read()
            d = r.to_mqtt()
            self.pub(f"{pfx}/pump_temp/value_c", d["pump_temp_c"])
            self.pub(f"{pfx}/pump_temp/state",   d["pump_temp_state"])
            self.pub(f"{pfx}/pump_temp/trend",   d["pump_temp_trend"])
            self.pub(f"{pfx}/pump_temp/alert",   str(d["pump_temp_alert"]).lower())
            self.pub(f"{pfx}/pump_temp/critical",str(d["pump_temp_crit"]).lower())
            if r.alarm_crit:
                self.pub(f"{pfx}/alarm/active",  "true")
                self.pub(f"{pfx}/alarm/message",
                         f"Température pompe critique : {r.temp_c:.1f}°C")
            elif r.alarm_alert:
                self.pub(f"{pfx}/alarm/message",
                         f"Température pompe élevée : {r.temp_c:.1f}°C")
        except DS18B20Error as e:
            logger.error(f"[1-Wire] {e}")
            self.pub(f"{pfx}/pump_temp/state", "error")

    def _cycle_electrolyzer(self):
        """Met à jour le relais électrolyseur."""
        pfx = C.MQTT_PREFIX
        orp_alarm = self._last_orp_result.get("orp_alarm", False)
        self.electro.update(
            pump_running = self.drive.is_running,
            orp_alarm    = orp_alarm,
        )
        st = self.electro.status()
        self.pub(f"{pfx}/electrolyzer/is_on",       str(st["is_on"]).lower())
        self.pub(f"{pfx}/electrolyzer/lock_reason",  st["lock_reason"] or "aucun")
        self.pub(f"{pfx}/electrolyzer/runtime_h",    st["runtime_hours"])
        self.pub(f"{pfx}/cmd/electrolyzer/state",
                 "ON" if st["enabled"] else "OFF")

    # ═══════════════════════════════════════════════════════════
    # Boucle principale
    # ═══════════════════════════════════════════════════════════

    def run(self):
        logger.info("[POOL] Boucle principale démarrée")

        # Planning initial
        try:
            t0 = self.temp_sensor.read()
            self._last_temp = t0
            self.filtration.build_schedule(t0)
        except AtlasSensorError:
            self.filtration.build_schedule(25.0)

        while self._running:
            now = time.time()

            # ── Mise à jour filtration (chaque cycle) ─────────
            self.filtration.update(self._last_temp)

            # ── Électrolyseur (chaque cycle) ──────────────────
            self._cycle_electrolyzer()

            # ── Capteurs Atlas + régulation ───────────────────
            if now - self._t_sensors >= C.INTERVAL_SENSORS:
                self._t_sensors = now
                self._cycle_sensors()

            # ── Télémétrie variateur ──────────────────────────
            if now - self._t_drive >= C.INTERVAL_DRIVE:
                self._t_drive = now
                self._cycle_drive()

            # ── Température pompe ─────────────────────────────
            if now - self._t_pump_temp >= C.INTERVAL_PUMP_TEMP:
                self._t_pump_temp = now
                self._cycle_pump_temp()

            # ── Chrono amorçage LCD ───────────────────────────
            if self._prime_active and self.display:
                self.display.update_prime_chrono()

            time.sleep(C.LOOP_SLEEP)

        self._shutdown()

    # ═══════════════════════════════════════════════════════════
    # Arrêt propre
    # ═══════════════════════════════════════════════════════════

    def _sig_handler(self, sig, frame):
        logger.info(f"[POOL] Signal {sig} — arrêt en cours")
        self._running = False

    def _shutdown(self):
        logger.info("[POOL] Arrêt propre...")
        try:
            if self.drive.is_running: self.drive.ramp_stop()
            self.peri_pump.stop()
            self.electro.disable()
            self.pcf.close()
            self.drive.close()
            for s in (self.ph_sensor, self.orp_sensor, self.temp_sensor, self.peri_pump):
                s.close()
            if self.display: self.display.lcd.close()
            if self.buttons: self.buttons.close()
        except Exception as e:
            logger.error(f"[POOL] Arrêt matériel : {e}")
        try:
            self.mqtt.publish(f"{C.MQTT_PREFIX}/status", "offline", qos=1, retain=True)
            self.mqtt.loop_stop()
            self.mqtt.disconnect()
        except Exception:
            pass
        logger.info("[POOL] Arrêt terminé")


# ─────────────────────────────────────────────────────────────────────────────
if __name__ == "__main__":
    PoolController().run()
