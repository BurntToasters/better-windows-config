using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Reports a failed registry operation. The operation, path, Win32 code, system message, and any
/// additional detail are shown in one selectable block.
/// </summary>
public sealed class ErrorDialog : ModalDialogWindow
{
    public ErrorDialog(string title, string details)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(details);

        Title = title;
        SetHeading(title);
        PrimaryText = "OK";
        ShowSecondaryButton = false;
        SetPreferredSize(560, 300);

        var text = new TextBlock
        {
            Text = details,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        AutomationProperties.SetAutomationId(text, "DialogErrorText");
        SetBody(text);
    }

    /// <summary>Builds the message for a registry operation failure.</summary>
    public static ErrorDialog ForOperation(
        string title,
        string operation,
        string path,
        int win32Error,
        string? additionalDetail = null)
    {
        var lines = new List<string>
        {
            "Operation: " + operation,
            "Path: " + path,
        };

        if (win32Error >= 0)
        {
            string systemMessage = new Win32Exception(win32Error).Message;
            lines.Add($"Win32 error {win32Error}: {systemMessage}");
        }

        if (!string.IsNullOrWhiteSpace(additionalDetail))
        {
            lines.Add(string.Empty);
            lines.Add(additionalDetail);
        }

        return new ErrorDialog(title, string.Join(Environment.NewLine, lines));
    }

    /// <summary>Builds a message for a validation or general failure without a Win32 code.</summary>
    public static ErrorDialog ForMessage(string title, string message) =>
        new(title, message);

    protected override void OnDialogLoaded()
    {
        FocusPrimary();
    }
}
