using System.Collections.Generic;

namespace ConnectDesktop.Platform.Windows;

/// <summary>
/// Conversão entre códigos HID (página 0x07, USB HID Usage Tables) e scan codes PS/2 set 1 do Windows.
/// Scan codes "estendidos" usam o prefixo E0 e são identificados pela flag <c>extended</c>.
/// </summary>
public static class KeyMap
{
    private const ushort VkPause = 0x13;
    private const ushort HidPause = 0x48;

    /// <summary>Tabela principal: HID, scan code e se é estendido (E0).</summary>
    private static readonly (ushort Hid, ushort Scan, bool Extended)[] Table =
    {
        // Letras A–Z
        (0x04, 0x1E, false), (0x05, 0x30, false), (0x06, 0x2E, false), (0x07, 0x20, false),
        (0x08, 0x12, false), (0x09, 0x21, false), (0x0A, 0x22, false), (0x0B, 0x23, false),
        (0x0C, 0x17, false), (0x0D, 0x24, false), (0x0E, 0x25, false), (0x0F, 0x26, false),
        (0x10, 0x32, false), (0x11, 0x31, false), (0x12, 0x18, false), (0x13, 0x19, false),
        (0x14, 0x10, false), (0x15, 0x13, false), (0x16, 0x1F, false), (0x17, 0x14, false),
        (0x18, 0x16, false), (0x19, 0x2F, false), (0x1A, 0x11, false), (0x1B, 0x2D, false),
        (0x1C, 0x15, false), (0x1D, 0x2C, false),
        // Dígitos 1–9, 0
        (0x1E, 0x02, false), (0x1F, 0x03, false), (0x20, 0x04, false), (0x21, 0x05, false),
        (0x22, 0x06, false), (0x23, 0x07, false), (0x24, 0x08, false), (0x25, 0x09, false),
        (0x26, 0x0A, false), (0x27, 0x0B, false),
        // Controles e pontuação
        (0x28, 0x1C, false), // Enter
        (0x29, 0x01, false), // Esc
        (0x2A, 0x0E, false), // Backspace
        (0x2B, 0x0F, false), // Tab
        (0x2C, 0x39, false), // Espaço
        (0x2D, 0x0C, false), // -
        (0x2E, 0x0D, false), // =
        (0x2F, 0x1A, false), // [
        (0x30, 0x1B, false), // ]
        (0x31, 0x2B, false), // \
        (0x33, 0x27, false), // ;
        (0x34, 0x28, false), // '
        (0x35, 0x29, false), // `
        (0x36, 0x33, false), // ,
        (0x37, 0x34, false), // .
        (0x38, 0x35, false), // /
        (0x39, 0x3A, false), // CapsLock
        // F1–F12
        (0x3A, 0x3B, false), (0x3B, 0x3C, false), (0x3C, 0x3D, false), (0x3D, 0x3E, false),
        (0x3E, 0x3F, false), (0x3F, 0x40, false), (0x40, 0x41, false), (0x41, 0x42, false),
        (0x42, 0x43, false), (0x43, 0x44, false), (0x44, 0x57, false), (0x45, 0x58, false),
        (0x46, 0x37, true),  // PrintScreen (E0 37)
        (0x47, 0x46, false), // ScrollLock
        // Pause: entrada no hook é tratada pelo vkCode 0x13 (ver FromHookEvent); aqui só o sentido HID -> scan
        // (não entra na tabela reversa, pois 0x45 é NumLock)
        // Bloco de navegação
        (0x49, 0x52, true),  // Insert
        (0x4A, 0x47, true),  // Home
        (0x4B, 0x49, true),  // PageUp
        (0x4C, 0x53, true),  // Delete
        (0x4D, 0x4F, true),  // End
        (0x4E, 0x51, true),  // PageDown
        (0x4F, 0x4D, true),  // Right
        (0x50, 0x4B, true),  // Left
        (0x51, 0x50, true),  // Down
        (0x52, 0x48, true),  // Up
        // Teclado numérico
        (0x53, 0x45, false), // NumLock
        (0x54, 0x35, true),  // KP /
        (0x55, 0x37, false), // KP *
        (0x56, 0x4A, false), // KP -
        (0x57, 0x4E, false), // KP +
        (0x58, 0x1C, true),  // KP Enter
        (0x59, 0x4F, false), // KP 1
        (0x5A, 0x50, false), // KP 2
        (0x5B, 0x51, false), // KP 3
        (0x5C, 0x4B, false), // KP 4
        (0x5D, 0x4C, false), // KP 5
        (0x5E, 0x4D, false), // KP 6
        (0x5F, 0x47, false), // KP 7
        (0x60, 0x48, false), // KP 8
        (0x61, 0x49, false), // KP 9
        (0x62, 0x52, false), // KP 0
        (0x63, 0x53, false), // KP .
        (0x67, 0x59, false), // KP =
        // Teclas extras
        (0x64, 0x56, false), // Non-US \ (ISO)
        (0x65, 0x5D, true),  // Application / Menu (E0 5D)
        // F13–F24
        (0x68, 0x64, false), (0x69, 0x65, false), (0x6A, 0x66, false), (0x6B, 0x67, false),
        (0x6C, 0x68, false), (0x6D, 0x69, false), (0x6E, 0x6A, false), (0x6F, 0x6B, false),
        (0x70, 0x6C, false), (0x71, 0x6D, false), (0x72, 0x6E, false), (0x73, 0x76, false),
        // Teclas ABNT2
        (0x85, 0x7E, false), // ABNT2 ponto do teclado numérico
        (0x87, 0x73, false), // International1 (ABNT2 "/?")
        // Modificadores (E0–E7)
        (0xE0, 0x1D, false), // LeftCtrl
        (0xE1, 0x2A, false), // LeftShift
        (0xE2, 0x38, false), // LeftAlt
        (0xE3, 0x5B, true),  // LeftGUI (E0 5B)
        (0xE4, 0x1D, true),  // RightCtrl (E0 1D)
        (0xE5, 0x36, false), // RightShift
        (0xE6, 0x38, true),  // RightAlt (E0 38)
        (0xE7, 0x5C, true),  // RightGUI (E0 5C)
    };

    private static readonly Dictionary<ushort, (ushort Scan, bool Extended)> HidToScanMap = new();
    private static readonly Dictionary<(ushort Scan, bool Extended), ushort> ScanToHidMap = new();

    static KeyMap()
    {
        foreach (var entry in Table)
        {
            HidToScanMap.Add(entry.Hid, (entry.Scan, entry.Extended));
            ScanToHidMap.Add((entry.Scan, entry.Extended), entry.Hid);
        }

        // Pause: o sentido HID -> scan usa 0x45 (como o Windows reporta); a volta é feita pelo vkCode no hook.
        HidToScanMap.Add(HidPause, (0x45, false));
        // NumLock pode chegar com a flag estendida dependendo do hook; trata como o mesmo HID.
        ScanToHidMap.Add((0x45, true), 0x53);
    }

    /// <summary>Converte um scan code do Windows em código HID. Retorna null se não houver equivalente.</summary>
    public static ushort? ScanToHid(ushort scan, bool extended)
    {
        return ScanToHidMap.TryGetValue((scan, extended), out var hid) ? hid : null;
    }

    /// <summary>Converte um código HID em scan code do Windows. Retorna null se não houver equivalente.</summary>
    public static (ushort scan, bool extended)? HidToScan(ushort hid)
    {
        if (HidToScanMap.TryGetValue(hid, out var entry))
        {
            return (entry.Scan, entry.Extended);
        }
        return null;
    }

    /// <summary>
    /// Converte um evento do hook de teclado (vkCode, scanCode, flag estendida) em HID.
    /// Pause é reconhecido pelo vkCode; se o scan vier 0, usa o vkCode como fallback.
    /// </summary>
    internal static ushort? FromHookEvent(uint vkCode, uint scanCode, bool extended)
    {
        if (vkCode == VkPause)
        {
            return HidPause;
        }
        if (scanCode == 0 || scanCode > 0xFF)
        {
            return VkToHid(vkCode);
        }
        return ScanToHid((ushort)scanCode, extended) ?? VkToHid(vkCode);
    }

    /// <summary>Fallback por código virtual (VK_*) para teclas que não têm scan code.</summary>
    internal static ushort? VkToHid(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (ushort)(0x04 + (vk - 0x41));   // A–Z
        if (vk >= 0x31 && vk <= 0x39) return (ushort)(0x1E + (vk - 0x31));   // 1–9
        if (vk == 0x30) return 0x27;                                          // 0
        if (vk >= 0x70 && vk <= 0x7B) return (ushort)(0x3A + (vk - 0x70));   // F1–F12
        if (vk >= 0x7C && vk <= 0x87) return (ushort)(0x68 + (vk - 0x7C));   // F13–F24

        switch (vk)
        {
            case 0x0D: return 0x28; // Enter
            case 0x1B: return 0x29; // Esc
            case 0x08: return 0x2A; // Backspace
            case 0x09: return 0x2B; // Tab
            case 0x20: return 0x2C; // Espaço
            case 0x14: return 0x39; // CapsLock
            case 0x2C: return 0x46; // PrintScreen
            case 0x91: return 0x47; // ScrollLock
            case 0x13: return HidPause;
            case 0x2D: return 0x49; // Insert
            case 0x24: return 0x4A; // Home
            case 0x21: return 0x4B; // PageUp
            case 0x2E: return 0x4C; // Delete
            case 0x23: return 0x4D; // End
            case 0x22: return 0x4E; // PageDown
            case 0x27: return 0x4F; // Right
            case 0x25: return 0x50; // Left
            case 0x28: return 0x51; // Down
            case 0x26: return 0x52; // Up
            case 0x90: return 0x53; // NumLock
            case 0x5D: return 0x65; // Application
            case 0xA0: return 0xE1; // LShift
            case 0xA1: return 0xE5; // RShift
            case 0xA2: return 0xE0; // LCtrl
            case 0xA3: return 0xE4; // RCtrl
            case 0xA4: return 0xE2; // LAlt
            case 0xA5: return 0xE6; // RAlt
            case 0x5B: return 0xE3; // LWin
            case 0x5C: return 0xE7; // RWin
            default: return null;
        }
    }
}
