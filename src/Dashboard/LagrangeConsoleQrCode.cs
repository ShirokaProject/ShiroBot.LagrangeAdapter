using System.Text;
using Net.Codecrete.QrCodeGenerator;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeConsoleQrCode
{
    internal static string Render(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var qr = QrCode.EncodeText(url, QrCode.Ecc.Low);
        const int border = 4;
        var result = new StringBuilder();
        // Two module rows per character keep the QR square in a monospace terminal.
        // Include the required quiet zone; no colour/control codes enter host log files.
        for (var y = -border; y < qr.Size + border; y += 2)
        {
            for (var x = -border; x < qr.Size + border; x++)
                result.Append((qr.GetModule(x, y), qr.GetModule(x, y + 1)) switch
                {
                    (true, true) => '█', (true, false) => '▀',
                    (false, true) => '▄', _ => ' '
                });
            result.AppendLine();
        }
        return result.ToString();
    }
}
