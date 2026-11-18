using Bwc.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Bwc.Hub.Updates;

/// <summary>
/// Shows the Windows toast that tells the user the Hub is closing to install an update. The app closes and
/// later reappears, so the toast gives the reason. The toast has no buttons and no activation handling,
/// because the Hub exits right after it is shown. The toast never blocks or fails the update:
/// <see cref="ShowUpdating"/> never throws. A failure is logged as a warning, and the install goes on.
/// </summary>
internal static class UpdateNotice
{
    /// <summary>Shows the "updating" toast. Never throws: a failure is logged and the caller continues.</summary>
    public static void ShowUpdating()
    {
        try
        {
            string title = HubStrings.Get("ProductName");
            string body = HubStrings.Get("UpdateInstallingToast");

            // Text is added as text nodes, so no value is ever concatenated into the XML.
            var xml = new XmlDocument();
            XmlElement toast = xml.CreateElement("toast");
            xml.AppendChild(toast);
            XmlElement visual = xml.CreateElement("visual");
            toast.AppendChild(visual);
            XmlElement binding = xml.CreateElement("binding");
            binding.SetAttribute("template", "ToastGeneric");
            visual.AppendChild(binding);
            AppendText(xml, binding, title);
            AppendText(xml, binding, body);

            ToastNotifier notifier = ToastNotificationManager.CreateToastNotifier(AppIdentity.HubAppUserModelId);
            notifier.Show(new ToastNotification(xml));
            Log.Info("Update notification shown.");
        }
        catch (Exception ex)
        {
            Log.Warn("Update notification could not be shown. The update continues. " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void AppendText(XmlDocument xml, XmlElement binding, string value)
    {
        XmlElement text = xml.CreateElement("text");
        text.AppendChild(xml.CreateTextNode(value));
        binding.AppendChild(text);
    }
}
