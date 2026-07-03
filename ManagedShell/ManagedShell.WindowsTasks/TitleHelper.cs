using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ManagedShell.WindowsTasks;

internal static class TitleHelper
{
    // Ловит:
    //   C:\dir\file.ext,  D:/dir/file.ext
    //   \\server\share\dir\file.ext         (UNC)
    //   .\rel\file.ext,   ..\rel\file.ext   (относительные с якорем)
    private static readonly Regex WindowsFilePath = new(
        @"(?:" +
            @"[A-Za-z]:[\\/]" +                                       // диск: C:\ или C:/
            @"|\\\\[^\\/:*?""<>|\s]+[\\/][^\\/:*?""<>|\s]+[\\/]" +    // UNC: \\server\share\
            @"|\.{1,2}[\\/]" +                                        // .\ или ..\
        @")" +
        @"(?:[^\\/:*?""<>|\s]+[\\/])*" +   // промежуточные папки
        @"[^\\/:*?""<>|\s]+\.[A-Za-z0-9]+", // имя файла + расширение
        RegexOptions.Compiled);

    internal static string GetTitle(string title, out TaskBarDisplayType taskBarDisplayType)
    {        
        if (string.IsNullOrEmpty(title))
        {
            taskBarDisplayType = TaskBarDisplayType.Left;
            return string.Empty;
        }

        var m = WindowsFilePath.Match(title);
        if (!m.Success)
        {
            taskBarDisplayType = TaskBarDisplayType.Left;
            return title;
        }

        taskBarDisplayType = TaskBarDisplayType.Right;
        return m.Value;        
    }
}
