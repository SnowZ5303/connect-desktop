#nullable enable
using System;
using System.Collections.Generic;

namespace ConnectDesktop.Platform.Linux;

/// <summary>
/// Conversão entre códigos HID (USB HID Usage Tables, página 0x07) e keycodes do X11.
/// Códigos evdev do kernel (linux/input-event-codes.h) são os valores da tabela;
/// keycode X11 = evdev + 8.
/// </summary>
internal static class KeyMap
{
    private const uint X11Offset = 8;

    // (HID, KEY_* evdev)
    private static readonly (ushort Hid, ushort Evdev)[] Pairs =
    {
        // Letras A-Z
        (0x04, 30), (0x05, 48), (0x06, 46), (0x07, 32), (0x08, 18), (0x09, 33), (0x0A, 34), (0x0B, 35),
        (0x0C, 23), (0x0D, 36), (0x0E, 37), (0x0F, 38), (0x10, 50), (0x11, 49), (0x12, 24), (0x13, 25),
        (0x14, 16), (0x15, 19), (0x16, 31), (0x17, 20), (0x18, 22), (0x19, 47), (0x1A, 17), (0x1B, 45),
        (0x1C, 21), (0x1D, 44),
        // Números 1-9, 0
        (0x1E, 2), (0x1F, 3), (0x20, 4), (0x21, 5), (0x22, 6), (0x23, 7), (0x24, 8), (0x25, 9), (0x26, 10),
        (0x27, 11),
        // Controle e pontuação
        (0x28, 28),  // KEY_ENTER
        (0x29, 1),   // KEY_ESC
        (0x2A, 14),  // KEY_BACKSPACE
        (0x2B, 15),  // KEY_TAB
        (0x2C, 57),  // KEY_SPACE
        (0x2D, 12),  // KEY_MINUS
        (0x2E, 13),  // KEY_EQUAL
        (0x2F, 26),  // KEY_LEFTBRACE
        (0x30, 27),  // KEY_RIGHTBRACE
        (0x31, 43),  // KEY_BACKSLASH
        (0x33, 39),  // KEY_SEMICOLON
        (0x34, 40),  // KEY_APOSTROPHE
        (0x35, 41),  // KEY_GRAVE
        (0x36, 51),  // KEY_COMMA
        (0x37, 52),  // KEY_DOT
        (0x38, 53),  // KEY_SLASH
        (0x39, 58),  // KEY_CAPSLOCK
        // F1-F12
        (0x3A, 59), (0x3B, 60), (0x3C, 61), (0x3D, 62), (0x3E, 63), (0x3F, 64),
        (0x40, 65), (0x41, 66), (0x42, 67), (0x43, 68), (0x44, 87), (0x45, 88),
        // Sistema e navegação
        (0x46, 99),   // KEY_SYSRQ (PrintScreen)
        (0x47, 70),   // KEY_SCROLLLOCK
        (0x48, 119),  // KEY_PAUSE
        (0x49, 110),  // KEY_INSERT
        (0x4A, 102),  // KEY_HOME
        (0x4B, 104),  // KEY_PAGEUP
        (0x4C, 111),  // KEY_DELETE
        (0x4D, 107),  // KEY_END
        (0x4E, 109),  // KEY_PAGEDOWN
        (0x4F, 106),  // KEY_RIGHT
        (0x50, 105),  // KEY_LEFT
        (0x51, 108),  // KEY_DOWN
        (0x52, 103),  // KEY_UP
        // Teclado numérico
        (0x53, 69),   // KEY_NUMLOCK
        (0x54, 98),   // KEY_KPSLASH
        (0x55, 55),   // KEY_KPASTERISK
        (0x56, 74),   // KEY_KPMINUS
        (0x57, 78),   // KEY_KPPLUS
        (0x58, 96),   // KEY_KPENTER
        (0x59, 79),   // KEY_KP1
        (0x5A, 80),   // KEY_KP2
        (0x5B, 81),   // KEY_KP3
        (0x5C, 75),   // KEY_KP4
        (0x5D, 76),   // KEY_KP5
        (0x5E, 77),   // KEY_KP6
        (0x5F, 71),   // KEY_KP7
        (0x60, 72),   // KEY_KP8
        (0x61, 73),   // KEY_KP9
        (0x62, 82),   // KEY_KP0
        (0x63, 83),   // KEY_KPDOT
        // Teclas não-US e especiais
        (0x64, 86),   // KEY_102ND
        (0x65, 127),  // KEY_COMPOSE (Application)
        (0x66, 116),  // KEY_POWER
        (0x67, 117),  // KEY_KPEQUAL
        // F13-F24
        (0x68, 183), (0x69, 184), (0x6A, 185), (0x6B, 186), (0x6C, 187), (0x6D, 188),
        (0x6E, 189), (0x6F, 190), (0x70, 191), (0x71, 192), (0x72, 193), (0x73, 194),
        // Teclas internacionais (JIS)
        (0x85, 121),  // KEY_KPCOMMA
        (0x87, 89),   // KEY_RO
        (0x88, 90),   // KEY_KATAKANAHIRAGANA
        (0x89, 124),  // KEY_YEN
        (0x8A, 92),   // KEY_HENKAN
        (0x8B, 94),   // KEY_MUHENKAN
        // Modificadores
        (0xE0, 29),   // KEY_LEFTCTRL
        (0xE1, 42),   // KEY_LEFTSHIFT
        (0xE2, 56),   // KEY_LEFTALT
        (0xE3, 125),  // KEY_LEFTMETA
        (0xE4, 97),   // KEY_RIGHTCTRL
        (0xE5, 54),   // KEY_RIGHTSHIFT
        (0xE6, 100),  // KEY_RIGHTALT
        (0xE7, 126),  // KEY_RIGHTMETA
    };

    private static readonly Dictionary<ushort, ushort> HidToEvdev = new();
    private static readonly Dictionary<ushort, ushort> EvdevToHid = new();

    static KeyMap()
    {
        foreach (var (hid, evdev) in Pairs)
        {
            HidToEvdev[hid] = evdev;
            EvdevToHid[evdev] = hid;
        }
    }

    /// <summary>Keycode X11 → código HID. Retorna null se a tecla não tiver equivalente.</summary>
    public static ushort? X11ToHid(uint keycode)
    {
        if (keycode < X11Offset) return null;
        uint evdev = keycode - X11Offset;
        if (evdev > ushort.MaxValue) return null;
        if (EvdevToHid.TryGetValue((ushort)evdev, out ushort hid)) return hid;
        return null;
    }

    /// <summary>Código HID → keycode X11. Retorna null se a tecla não tiver equivalente.</summary>
    public static uint? HidToX11(ushort hid)
    {
        if (HidToEvdev.TryGetValue(hid, out ushort evdev)) return evdev + X11Offset;
        return null;
    }
}
