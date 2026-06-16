using System.Device.I2c;
using System.Globalization;
using System.Text;

namespace PiscineController.Hardware;

public sealed class Lcd1602 : IDisposable
{
    private readonly I2cDevice _device;
    private bool _backlight = true;

    private const byte LCD_BACKLIGHT = 0x08;
    private const byte ENABLE = 0x04;
    private const byte RS_DATA = 0x01;

    public Lcd1602(int busId, int address)
    {
        _device = I2cDevice.Create(
            new I2cConnectionSettings(busId, address));
    }

    public void Initialize()
    {
        Thread.Sleep(50);
        Init();
    }

    private void Init()
    {
        WriteFourBits(0x03);
        Thread.Sleep(5);

        WriteFourBits(0x03);
        Thread.Sleep(1);

        WriteFourBits(0x03);
        Thread.Sleep(1);

        WriteFourBits(0x02);

        SendCommand(0x28); // 4 bits, 2 lignes
        SendCommand(0x0C); // affichage ON, curseur OFF
        SendCommand(0x06); // incrément curseur
        SendCommand(0x01); // effacement

        Thread.Sleep(2);
    }

    public void Clear()
    {
        SendCommand(0x01);
        Thread.Sleep(2);
    }

    public void SetCursor(int col, int row)
    {
        int[] offsets = { 0x00, 0x40 };
        SendCommand((byte)(0x80 | (col + offsets[row & 1])));
    }

    public void Print(string text)
    {
        text = NormalizeText(text);

        foreach (char c in text)
        {
            SendChar(c switch
            {
                '°' => 0xDF, // symbole degré HD44780
                _ => (byte)(c <= 255 ? c : '?')
            });
        }
    }

    public void PrintLine(int row, string text)
    {
        SetCursor(0, row);

        text = NormalizeText(text);

        if (text.Length > 16)
            text = text[..16];

        text = text.PadRight(16);

        Print(text);
    }

    public void SetBacklight(bool on)
    {
        _backlight = on;
        _device.WriteByte((byte)(_backlight ? LCD_BACKLIGHT : 0));
    }

    private static string NormalizeText(string text)
    {
        string normalized = text.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder();

        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString();
    }

    private void SendCommand(byte cmd)
    {
        SendByte(cmd, 0);
    }

    private void SendChar(byte data)
    {
        SendByte(data, RS_DATA);
    }

    private void SendByte(byte val, byte mode)
    {
        byte hi = (byte)(val & 0xF0);
        byte lo = (byte)((val << 4) & 0xF0);

        WriteFourBits((byte)(hi | mode));
        WriteFourBits((byte)(lo | mode));
    }

    private void WriteFourBits(byte val)
    {
        byte bl = _backlight ? LCD_BACKLIGHT : (byte)0;

        _device.WriteByte((byte)(val | bl | ENABLE));
        Thread.Sleep(1);

        _device.WriteByte((byte)((val | bl) & ~ENABLE));
    }

    public void Dispose()
    {
        _device.Dispose();
    }
}
