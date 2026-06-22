using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ManagedShell.WindowsTasks
{
    internal static class TitleHelper
    {
        internal static string GetTitle(string title, out TaskBarDisplayType taskBarDisplayType)
        {
            const string c1 = " - Notepad++";
            if (title.EndsWith(c1, StringComparison.InvariantCultureIgnoreCase))
            {
                title = title.Substring(0, title.Length - c1.Length);
            }

            if (Uri.TryCreate(title, UriKind.Absolute, out Uri uri))
            {
                taskBarDisplayType = TaskBarDisplayType.Right;
            }
            else
            {
                taskBarDisplayType = TaskBarDisplayType.Left;
            }

            return title;
        }
    }
}
