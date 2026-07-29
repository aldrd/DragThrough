using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ZombieBar.Utilities;

public static class OrderHelper
{
    public static string GetOrder(string insertAfter_Order, string insertBefore_Order)
    {
        // 2023.09.18
        string order = "";
        bool insertBeforeMax = false;
        // Two-digits groups 
        int maxGroupsCount = Math.Max(insertAfter_Order.Length / 2, insertBefore_Order.Length / 2);
        foreach (int groupIndex in Enumerable.Range(0, maxGroupsCount + 1))
        {
            string insertAfterHex = SafeSubstring(insertAfter_Order, 2 * groupIndex, 2);
            string insertBeforeHex = SafeSubstring(insertBefore_Order, 2 * groupIndex, 2);
            if (insertAfterHex.Length < 2 || !Int32.TryParse(insertAfterHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int insertAfterInt))
                insertAfterInt = 0;
            if (insertBeforeMax || insertBeforeHex.Length < 2 || !Int32.TryParse(insertBeforeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int insertBeforeInt))
                insertBeforeInt = 256;
            if (insertAfterInt == insertBeforeInt)
            {
                order += insertAfterInt.ToString("x2"); // HEX
                continue;
            }
            if (insertAfterInt + 1 == insertBeforeInt)
            {
                order += insertAfterInt.ToString("x2"); // HEX
                insertBeforeMax = true;
                continue;
            }
            return order + ((insertBeforeInt + insertAfterInt) / 2).ToString("x2"); // HEX
        }

        throw new InvalidOperationException();
    }

    private static string SafeSubstring(string str, int startIndex, int length)
    {
        if (String.IsNullOrEmpty(str) || startIndex >= str!.Length)
            return @"";
        if (startIndex < 0)
            startIndex = 0;
        return str.Substring(startIndex, Math.Min(length, str!.Length - startIndex));
    }
}
