// Dono: g04. Confere a permissão de Acessibilidade do macOS (exigida pelo CGEventTap e pelos eventos injetados).
namespace ConnectDesktop.Platform.Mac;

internal static class MacPermissions
{
    internal const string PermissionMessage =
        "Permita o Connect Desktop em Ajustes do Sistema > Privacidade e Segurança > Acessibilidade e também em Monitoramento de Entrada, depois reabra o app.";

    /// <summary>Verifica a permissão sem pedir ao sistema para abrir o diálogo. Nunca lança.</summary>
    internal static bool IsTrusted()
    {
        try
        {
            return MacNative.AXIsProcessTrusted();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Confere a permissão e pede ao sistema para abrir o pedido (kAXTrustedCheckOptionPrompt = true).
    /// Retorna false com a mensagem de permissão se faltar acesso ou se a verificação nativa falhar.
    /// </summary>
    internal static bool Check(out string message)
    {
        try
        {
            IntPtr options = CreatePromptOptions();
            try
            {
                if (MacNative.AXIsProcessTrustedWithOptions(options))
                {
                    message = string.Empty;
                    return true;
                }
            }
            finally
            {
                MacNative.CFRelease(options);
            }

            message = PermissionMessage;
            return false;
        }
        catch
        {
            message = PermissionMessage;
            return false;
        }
    }

    private static IntPtr CreatePromptOptions()
    {
        IntPtr[] keys = { MacNative.AXTrustedCheckOptionPrompt };
        IntPtr[] values = { MacNative.CFBooleanTrue };
        IntPtr dictionary = MacNative.CFDictionaryCreate(
            IntPtr.Zero,
            keys,
            values,
            1,
            MacNative.CFTypeDictionaryKeyCallBacks,
            MacNative.CFTypeDictionaryValueCallBacks);
        if (dictionary == IntPtr.Zero)
        {
            throw new InvalidOperationException("Não foi possível criar o dicionário de opções do macOS.");
        }
        return dictionary;
    }
}
