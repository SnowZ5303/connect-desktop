using System;
using System.Runtime.InteropServices;
using System.Threading;
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Windows;

/// <summary>Área de transferência de texto (CF_UNICODETEXT) no Windows. Nunca lança exceção.</summary>
public sealed class WindowsClipboard : IClipboard
{
    private const int OpenAttempts = 10;
    private const int OpenDelayMs = 20;

    /// <summary>Retorna o texto da área de transferência, ou null se não houver texto ou em caso de erro.</summary>
    public string? GetText()
    {
        try
        {
            if (!OpenWithRetry())
            {
                return null;
            }

            try
            {
                IntPtr handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr pointer = NativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Substitui o conteúdo da área de transferência pelo texto informado. Em erro, ignora.</summary>
    public void SetText(string text)
    {
        if (text is null)
        {
            return;
        }

        try
        {
            if (!OpenWithRetry())
            {
                return;
            }

            try
            {
                NativeMethods.EmptyClipboard();

                int byteCount = (text.Length + 1) * sizeof(char);
                IntPtr handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)(uint)byteCount);
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                IntPtr pointer = NativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(handle);
                    return;
                }

                try
                {
                    // Último caractere fica em 0: terminador nulo.
                    char[] chars = new char[text.Length + 1];
                    text.CopyTo(0, chars, 0, text.Length);
                    Marshal.Copy(chars, 0, pointer, chars.Length);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }

                // Em caso de sucesso, o sistema passa a ser dono do bloco.
                if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, handle) == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(handle);
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch
        {
            // Erros da área de transferência são ignorados.
        }
    }

    private static bool OpenWithRetry()
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            if (attempt < OpenAttempts - 1)
            {
                Thread.Sleep(OpenDelayMs);
            }
        }

        return false;
    }
}
