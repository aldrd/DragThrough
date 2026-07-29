using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ManagedShell.WindowsTasks;

internal static class TitleHelper
{
    // Ловит (в т.ч. с пробелами в именах папок/файла):
    //   C:\dir\file.ext,  D:/dir/file.ext
    //   C:\Program Files\My App\my file.txt
    //   \\server\share\dir\file.ext         (UNC)
    //   .\rel\file.ext,   ..\rel\file.ext   (относительные с якорем)
    // Якорь — буква диска / \\ / .\ в начале, расширение файла в конце. В именах разрешены пробелы,
    // запрещены недопустимые для путей символы и управляющие (\x00-\x1F), поэтому совпадение не
    // перетекает на перенос строки, но останавливается на суффиксе " - Имя приложения" (у него нет
    // точки-расширения, на которую опирается конец шаблона).
    private const string PathChar = @"[^\\/:*?""<>|\x00-\x1F]"; // любой символ имени, включая пробел
    private static readonly Regex WindowsFilePath = new(
        @"(?:" +
            @"[A-Za-z]:[\\/]" +                                  // диск: C:\ или C:/
            @"|\\\\" + PathChar + @"+[\\/]" + PathChar + @"+[\\/]" + // UNC: \\server\share\
            @"|\.{1,2}[\\/]" +                                   // .\ или ..\
        @")" +
        @"(?:" + PathChar + @"+[\\/])*" +   // промежуточные папки
        PathChar + @"+\.[A-Za-z0-9]+",      // имя файла + расширение
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
