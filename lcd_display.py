# lcd_display.py — Driver LCD1602 I2C (PCF8574 @ 0x27) + affichages piscine
"""
Pilotage d'un afficheur LCD 1602 via adaptateur I2C PCF8574.
Pinout PCF8574 → LCD HD44780 standard :
  P0=RS  P1=RW  P2=EN  P3=BL(rétroéclairage)
  P4=D4  P5=D5  P6=D6  P7=D7
"""

import time, threading, logging

logger = logging.getLogger("lcd")

# Bits de contrôle
LCD_RS   = 0x01
LCD_RW   = 0x02
LCD_EN   = 0x04
LCD_BL   = 0x08
LCD_D4   = 0x10
LCD_D5   = 0x20
LCD_D6   = 0x40
LCD_D7   = 0x80

# Commandes HD44780
CMD_CLEAR      = 0x01
CMD_HOME       = 0x02
CMD_ENTRY      = 0x06
CMD_DISPLAY_ON = 0x0C
CMD_FUNC_SET   = 0x28   # 4-bit, 2 lignes, 5x8
CMD_LINE1      = 0x80
CMD_LINE2      = 0xC0

E_PULSE  = 0.0005
E_DELAY  = 0.0005


class LCDError(Exception):
    pass


class LCD1602:
    """
    Driver LCD 1602 I2C (backpack PCF8574).
    Thread-safe via Lock interne.
    """
    def __init__(self, address: int = 0x27, bus: int = 1,
                 cols: int = 16, rows: int = 2):
        try:
            import smbus2
            self._bus = smbus2.SMBus(bus)
        except ImportError:
            raise LCDError("pip install smbus2")
        except Exception as e:
            raise LCDError(f"I2C bus {bus} : {e}")

        self.address   = address
        self.cols      = cols
        self.rows      = rows
        self._bl       = LCD_BL        # rétroéclairage ON
        self._lock     = threading.Lock()
        self._bl_timer = None

        self._init_lcd()
        logger.info(f"[LCD] Initialisé @ 0x{address:02X}")

    # ── Primitives I2C → LCD ──────────────────────────────────

    def _write_byte(self, data: int):
        self._bus.write_byte(self.address, data)
        time.sleep(E_DELAY)

    def _strobe(self, data: int):
        self._write_byte(data | LCD_EN)
        time.sleep(E_PULSE)
        self._write_byte(data & ~LCD_EN)
        time.sleep(E_PULSE)

    def _write4bits(self, data: int):
        self._write_byte(data | self._bl)
        self._strobe(data | self._bl)

    def _send(self, value: int, mode: int):
        """Envoie un octet en mode 4-bit (2 nibbles)."""
        high = mode | (value & 0xF0) | self._bl
        low  = mode | ((value << 4) & 0xF0) | self._bl
        self._write_byte(high)
        self._strobe(high)
        self._write_byte(low)
        self._strobe(low)

    def _cmd(self, cmd: int):
        self._send(cmd, 0x00)

    def _char(self, char: int):
        self._send(char, LCD_RS)

    def _init_lcd(self):
        time.sleep(0.05)
        self._write4bits(0x30)
        time.sleep(0.005)
        self._write4bits(0x30)
        time.sleep(0.001)
        self._write4bits(0x30)
        self._write4bits(0x20)              # passage 4-bit
        self._cmd(CMD_FUNC_SET)
        self._cmd(CMD_DISPLAY_ON)
        self._cmd(CMD_CLEAR)
        time.sleep(0.003)
        self._cmd(CMD_ENTRY)

    # ── API publique ──────────────────────────────────────────

    def clear(self):
        with self._lock:
            self._cmd(CMD_CLEAR)
            time.sleep(0.003)

    def write(self, row: int, text: str):
        """Écrit `text` sur la ligne `row` (0 ou 1), centré/tronqué à 16 chars."""
        with self._lock:
            text = text[:self.cols].ljust(self.cols)
            self._cmd(CMD_LINE1 if row == 0 else CMD_LINE2)
            for ch in text:
                self._char(ord(ch))

    def write2(self, line1: str, line2: str):
        """Écrit les deux lignes en une fois."""
        self.write(0, line1)
        self.write(1, line2)

    def backlight(self, on: bool):
        with self._lock:
            self._bl = LCD_BL if on else 0x00
            self._write_byte(self._bl)

    def backlight_timed(self, seconds: float):
        """Allume le rétroéclairage pendant N secondes puis l'éteint."""
        self.backlight(True)
        if self._bl_timer:
            self._bl_timer.cancel()
        self._bl_timer = threading.Timer(seconds, lambda: self.backlight(False))
        self._bl_timer.daemon = True
        self._bl_timer.start()

    def show_timed(self, line1: str, line2: str, duration: float):
        """Affiche un message pendant N secondes avec rétroéclairage."""
        self.write2(line1, line2)
        self.backlight_timed(duration)

    def close(self):
        try:
            if self._bl_timer:
                self._bl_timer.cancel()
            self.clear()
            self.backlight(False)
            self._bus.close()
        except Exception:
            pass


# ─────────────────────────────────────────────────────────────────────────────
# Affichages métier piscine
# ─────────────────────────────────────────────────────────────────────────────

class PoolDisplay:
    """
    Couche métier piscine au-dessus du driver LCD1602.
    Gère les affichages spécifiques et le chrono d'amorçage.
    """
    def __init__(self, lcd: LCD1602):
        self.lcd           = lcd
        self._prime_start  = 0.0
        self._prime_active = False
        self._prime_timer  = None

    # ── Mesures (bouton 1) ────────────────────────────────────

    def show_measures(self, temp_c: float, ph: float, orp_mv: float, duration: float):
        """Affiche T°/pH/ORP pendant `duration` secondes."""
        line1 = f"T:{temp_c:.1f}C pH:{ph:.2f}"
        line2 = f"ORP: {orp_mv:.0f} mV"
        self.lcd.show_timed(line1, line2, duration)
        logger.info(f"[LCD] Mesures : {line1} | {line2}")

    # ── Amorçage pompe doseuse (bouton 2) ─────────────────────

    def start_prime(self, max_s: float):
        """Lance l'affichage d'amorçage avec chrono."""
        self._prime_start  = __import__("time").time()
        self._prime_active = True
        self.lcd.backlight(True)
        self.lcd.write(0, "Amorcage pH-")
        self.lcd.write(1, "00s")
        self._prime_timer = __import__("threading").Timer(
            max_s, self.stop_prime
        )
        self._prime_timer.daemon = True
        self._prime_timer.start()
        logger.info("[LCD] Démarrage amorçage")

    def update_prime_chrono(self):
        """À appeler régulièrement pendant l'amorçage pour mettre à jour le chrono."""
        if not self._prime_active:
            return
        elapsed = int(__import__("time").time() - self._prime_start)
        self.lcd.write(1, f"{elapsed:03d}s")

    def stop_prime(self):
        if self._prime_timer:
            self._prime_timer.cancel()
        self._prime_active = False
        self.lcd.write(0, "Amorcage termine")
        self.lcd.write(1, "")
        __import__("threading").Timer(3, lambda: self.lcd.backlight(False)).start()
        logger.info("[LCD] Amorçage terminé")

    @property
    def prime_active(self) -> bool:
        return self._prime_active

    @property
    def prime_elapsed_s(self) -> float:
        if not self._prime_active: return 0.0
        return __import__("time").time() - self._prime_start

    # ── Filtration (boutons 3 & 4) ────────────────────────────

    def show_pause(self, duration: float):
        self.lcd.show_timed("Filtration", "en pause", duration)
        logger.info("[LCD] Filtration en pause")

    def show_resume(self, duration: float):
        self.lcd.show_timed("Reprise", "filtration auto", duration)
        logger.info("[LCD] Reprise filtration")

    # ── Informations génériques ───────────────────────────────

    def show_mode(self, mode: str, freq_hz: float = 0.0):
        line2 = f"{freq_hz:.0f}Hz" if freq_hz else ""
        self.lcd.write2(f"Mode: {mode.upper()}", line2)

    def show_alarm(self, message: str):
        self.lcd.backlight(True)
        self.lcd.write2("!! ALARME !!", message[:16])

    def show_boot(self):
        self.lcd.write2("Piscine v2.0", "Demarrage...")
        __import__("time").sleep(2)
        self.lcd.clear()
