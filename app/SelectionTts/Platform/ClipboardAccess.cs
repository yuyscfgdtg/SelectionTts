using System.Runtime.InteropServices;
using System.Windows.Forms;
using SelectionTts.Core;

namespace SelectionTts.Platform;

internal sealed record ClipboardSnapshot(string? Text, string? Html, string? Rtf)
{
    public bool IsEmpty => Text is null && Html is null && Rtf is null;
}

internal static class ClipboardAccess
{
    public static ClipboardSnapshot Capture()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return new ClipboardSnapshot(
                    ReadText(),
                    TryGetData(DataFormats.Html),
                    TryGetData(DataFormats.Rtf));
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
        }

        return new ClipboardSnapshot(null, null, null);
    }

    public static bool Restore(ClipboardSnapshot snapshot)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (snapshot.IsEmpty)
                {
                    Clipboard.Clear();
                    return true;
                }

                var data = new DataObject();
                if (snapshot.Text is not null)
                {
                    data.SetData(DataFormats.UnicodeText, snapshot.Text);
                    data.SetData(DataFormats.Text, snapshot.Text);
                }

                if (snapshot.Html is not null)
                {
                    data.SetData(DataFormats.Html, snapshot.Html);
                }

                if (snapshot.Rtf is not null)
                {
                    data.SetData(DataFormats.Rtf, snapshot.Rtf);
                }

                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
        }

        Log.Warn("恢复剪贴板失败");
        return false;
    }

    public static string? TryGetText()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return ReadText();
            }
            catch (ExternalException)
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }

    private static string? ReadText()
    {
        if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
        {
            return Clipboard.GetText(TextDataFormat.UnicodeText);
        }

        return Clipboard.ContainsText() ? Clipboard.GetText() : null;
    }

    private static string? TryGetData(string format)
    {
        try
        {
            return Clipboard.TryGetData<string>(format, out var value) ? value : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }
}
