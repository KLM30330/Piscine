# ha_discovery.py — Autodiscovery MQTT Home Assistant
"""
Génère les payloads de configuration autodiscovery pour toutes les
entités publiées par le contrôleur piscine.
Publiées une seule fois dans homeassistant/<type>/<device_id>_<uid>/config
"""

import config as C


def _device() -> dict:
    return {
        "identifiers" : [C.HA_DEVICE_ID],
        "name"        : C.HA_DEVICE_NAME,
        "model"       : C.HA_DEVICE_MODEL,
        "manufacturer": C.HA_DEVICE_MFR,
    }


def _sensor(uid, name, topic, unit=None, dev_class=None, icon=None, vt=None) -> tuple:
    p = {"unique_id": f"{C.HA_DEVICE_ID}_{uid}", "name": name,
         "state_topic": topic, "device": _device()}
    if unit:      p["unit_of_measurement"] = unit
    if dev_class: p["device_class"]        = dev_class
    if icon:      p["icon"]                = icon
    if vt:        p["value_template"]      = vt
    return (f"{C.MQTT_HA_DISC}/sensor/{C.HA_DEVICE_ID}_{uid}/config", p)


def _binary(uid, name, topic, on="true", off="false", dev_class=None, icon=None) -> tuple:
    p = {"unique_id": f"{C.HA_DEVICE_ID}_{uid}", "name": name,
         "state_topic": topic, "payload_on": on, "payload_off": off,
         "device": _device()}
    if dev_class: p["device_class"] = dev_class
    if icon:      p["icon"]         = icon
    return (f"{C.MQTT_HA_DISC}/binary_sensor/{C.HA_DEVICE_ID}_{uid}/config", p)


def _switch(uid, name, state_t, cmd_t, on="ON", off="OFF", icon=None) -> tuple:
    p = {"unique_id": f"{C.HA_DEVICE_ID}_{uid}", "name": name,
         "state_topic": state_t, "command_topic": cmd_t,
         "payload_on": on, "payload_off": off, "device": _device()}
    if icon: p["icon"] = icon
    return (f"{C.MQTT_HA_DISC}/switch/{C.HA_DEVICE_ID}_{uid}/config", p)


def _number(uid, name, cmd_t, state_t, mn, mx, step, unit=None, icon=None) -> tuple:
    p = {"unique_id": f"{C.HA_DEVICE_ID}_{uid}", "name": name,
         "command_topic": cmd_t, "state_topic": state_t,
         "min": mn, "max": mx, "step": step, "device": _device()}
    if unit: p["unit_of_measurement"] = unit
    if icon: p["icon"]                = icon
    return (f"{C.MQTT_HA_DISC}/number/{C.HA_DEVICE_ID}_{uid}/config", p)


def _select(uid, name, cmd_t, state_t, options: list, icon=None) -> tuple:
    p = {"unique_id": f"{C.HA_DEVICE_ID}_{uid}", "name": name,
         "command_topic": cmd_t, "state_topic": state_t,
         "options": options, "device": _device()}
    if icon: p["icon"] = icon
    return (f"{C.MQTT_HA_DISC}/select/{C.HA_DEVICE_ID}_{uid}/config", p)


def _button(uid, name, cmd_t, payload="PRESS", icon=None) -> tuple:
    p = {"unique_id": f"{C.HA_DEVICE_ID}_{uid}", "name": name,
         "command_topic": cmd_t, "payload_press": payload,
         "device": _device()}
    if icon: p["icon"] = icon
    return (f"{C.MQTT_HA_DISC}/button/{C.HA_DEVICE_ID}_{uid}/config", p)


def all_entities() -> list:
    """Retourne la liste complète des (topic, payload) autodiscovery."""
    pfx = C.MQTT_PREFIX
    ents = []

    # ────────────────────────────────────────────────────────
    # CAPTEURS EAU
    # ────────────────────────────────────────────────────────
    ents.append(_sensor("water_temp",  "Piscine Température eau",
        f"{pfx}/sensor/water_temp_c", unit="°C", dev_class="temperature"))
    ents.append(_sensor("ph",          "Piscine pH",
        f"{pfx}/sensor/ph", icon="mdi:ph"))
    ents.append(_sensor("orp",         "Piscine ORP",
        f"{pfx}/sensor/orp_mv", unit="mV", icon="mdi:lightning-bolt"))
    ents.append(_sensor("water_state", "Piscine état eau",
        f"{pfx}/sensor/water_state", icon="mdi:water-check"))

    # ────────────────────────────────────────────────────────
    # POMPE FILTRATION — état
    # ────────────────────────────────────────────────────────
    ents.append(_binary("pump_running", "Pompe filtration",
        f"{pfx}/pump/running", dev_class="running", icon="mdi:pump"))
    ents.append(_sensor("pump_freq",    "Pompe fréquence",
        f"{pfx}/pump/freq_hz", unit="Hz", icon="mdi:sine-wave"))
    ents.append(_sensor("pump_current", "Pompe courant",
        f"{pfx}/drive/out_current_a", unit="A", dev_class="current"))
    ents.append(_sensor("pump_power",   "Pompe puissance",
        f"{pfx}/drive/out_power_kw", unit="kW", dev_class="power"))
    ents.append(_sensor("pump_voltage", "Pompe tension sortie",
        f"{pfx}/drive/out_voltage_v", unit="V", dev_class="voltage"))
    ents.append(_sensor("pump_rpm",     "Pompe vitesse",
        f"{pfx}/drive/motor_rpm", unit="rpm", icon="mdi:rotate-right"))
    ents.append(_sensor("drive_temp",   "Variateur température",
        f"{pfx}/drive/drive_temp_c", unit="°C", dev_class="temperature"))
    ents.append(_sensor("drive_hours",  "Variateur heures marche",
        f"{pfx}/drive/run_time_h", unit="h", icon="mdi:timer"))
    ents.append(_binary("drive_fault",  "Variateur défaut",
        f"{pfx}/drive/is_fault", dev_class="problem", icon="mdi:alert"))
    ents.append(_sensor("drive_fault_label", "Variateur défaut label",
        f"{pfx}/drive/fault_label", icon="mdi:alert-circle"))
    ents.append(_sensor("drive_dc_bus", "Variateur bus DC",
        f"{pfx}/drive/dc_bus_v", unit="V", dev_class="voltage"))

    # ────────────────────────────────────────────────────────
    # TEMPÉRATURE POMPE (DS18B20)
    # ────────────────────────────────────────────────────────
    ents.append(_sensor("pump_temp",    "Pompe température",
        f"{pfx}/pump_temp/value_c", unit="°C", dev_class="temperature"))
    ents.append(_sensor("pump_temp_state","Pompe état thermique",
        f"{pfx}/pump_temp/state", icon="mdi:thermometer"))
    ents.append(_sensor("pump_temp_trend","Pompe tendance thermique",
        f"{pfx}/pump_temp/trend", icon="mdi:trending-up"))
    ents.append(_binary("pump_temp_alert","Pompe température élevée",
        f"{pfx}/pump_temp/alert", dev_class="heat", icon="mdi:thermometer-alert"))
    ents.append(_binary("pump_temp_crit", "Pompe température critique",
        f"{pfx}/pump_temp/critical", dev_class="problem", icon="mdi:thermometer-high"))

    # ────────────────────────────────────────────────────────
    # PLANNING FILTRATION
    # ────────────────────────────────────────────────────────
    ents.append(_sensor("filt_mode",    "Filtration mode",
        f"{pfx}/filtration/mode", icon="mdi:cog"))
    ents.append(_sensor("filt_hours",   "Filtration heures/jour",
        f"{pfx}/filtration/required_hours", unit="h", icon="mdi:clock"))
    ents.append(_sensor("filt_slots",   "Filtration créneaux",
        f"{pfx}/filtration/slots", icon="mdi:calendar-clock"))

    # ────────────────────────────────────────────────────────
    # pH — dosage
    # ────────────────────────────────────────────────────────
    ents.append(_sensor("ph_dose_last", "pH dernière dose",
        f"{pfx}/ph/dose_last_ml", unit="mL", icon="mdi:eyedropper"))
    ents.append(_sensor("ph_dose_total","pH total dosé session",
        f"{pfx}/ph/dose_total_ml", unit="mL", icon="mdi:beaker"))
    ents.append(_sensor("ph_dose_count","pH nombre de doses",
        f"{pfx}/ph/dose_count", icon="mdi:counter"))
    ents.append(_binary("peri_pump_on", "Pompe doseuse active",
        f"{pfx}/ph/dispensing", dev_class="running", icon="mdi:pump"))

    # ────────────────────────────────────────────────────────
    # ÉLECTROLYSEUR
    # ────────────────────────────────────────────────────────
    ents.append(_binary("electro_on",   "Électrolyseur actif",
        f"{pfx}/electrolyzer/is_on", dev_class="running",
        icon="mdi:lightning-bolt-circle"))
    ents.append(_sensor("electro_lock", "Électrolyseur verrou",
        f"{pfx}/electrolyzer/lock_reason", icon="mdi:lock"))
    ents.append(_sensor("electro_hours","Électrolyseur heures marche",
        f"{pfx}/electrolyzer/runtime_h", unit="h", icon="mdi:timer"))

    # ────────────────────────────────────────────────────────
    # ALARMES
    # ────────────────────────────────────────────────────────
    ents.append(_binary("alarm_active", "Piscine alarme active",
        f"{pfx}/alarm/active", dev_class="problem", icon="mdi:alert"))
    ents.append(_sensor("alarm_message","Piscine message alarme",
        f"{pfx}/alarm/message", icon="mdi:message-alert"))

    # ────────────────────────────────────────────────────────
    # COMMANDES DEPUIS HOME ASSISTANT
    # ────────────────────────────────────────────────────────

    # Mode de filtration (select)
    ents.append(_select("cmd_mode", "Filtration mode",
        f"{pfx}/cmd/mode/set", f"{pfx}/cmd/mode/state",
        options=["auto", "forced", "boost", "pause", "stop"],
        icon="mdi:cog"))

    # Fréquence forcée (number)
    ents.append(_number("cmd_freq", "Filtration fréquence forcée",
        f"{pfx}/cmd/freq/set", f"{pfx}/cmd/freq/state",
        mn=C.FREQ_MIN_ABSOLUTE, mx=C.FREQ_NOMINAL, step=1.0,
        unit="Hz", icon="mdi:sine-wave"))

    # Consigne pH (number)
    ents.append(_number("cmd_ph_target", "pH consigne",
        f"{pfx}/cmd/ph_target/set", f"{pfx}/cmd/ph_target/state",
        mn=6.8, mx=7.8, step=0.05, icon="mdi:target"))

    # Seuils ORP
    ents.append(_number("cmd_orp_low", "ORP seuil bas",
        f"{pfx}/cmd/orp_low/set", f"{pfx}/cmd/orp_low/state",
        mn=600, mx=700, step=5, unit="mV", icon="mdi:lightning-bolt"))
    ents.append(_number("cmd_orp_high", "ORP seuil haut",
        f"{pfx}/cmd/orp_high/set", f"{pfx}/cmd/orp_high/state",
        mn=700, mx=800, step=5, unit="mV", icon="mdi:lightning-bolt"))

    # Électrolyseur (switch)
    ents.append(_switch("cmd_electro", "Électrolyseur",
        f"{pfx}/cmd/electrolyzer/state", f"{pfx}/cmd/electrolyzer/set",
        icon="mdi:lightning-bolt-circle"))

    # Amorçage pompe doseuse (button)
    ents.append(_button("cmd_prime", "Pompe doseuse amorçage",
        f"{pfx}/cmd/prime", icon="mdi:pump"))

    # Calibration pH (button + select)
    ents.append(_select("cmd_cal_ph", "pH calibration",
        f"{pfx}/cmd/calibrate_ph/set", f"{pfx}/cmd/calibrate_ph/state",
        options=["mid_7", "low_4", "high_10", "clear"],
        icon="mdi:test-tube"))

    return ents
