using System.Drawing;
using System.Windows.Forms;

namespace Sing_box_UI
{
    internal static class FormIconHelper
    {
        public static Icon LoadApplicationIcon()
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null)
                {
                    return (Icon)icon.Clone();
                }
            }
            catch
            {
            }

            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
