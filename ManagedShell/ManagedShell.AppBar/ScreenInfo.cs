using Ssz.Utils.Wpf.WpfScreenHelper;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ManagedShell.AppBar
{
    /// <summary>
    ///     All coordinates in pixels.
    /// </summary>
    public class ScreenInfo
    {
        /// <summary>
        /// 
        /// </summary>
        public Rectangle Bounds { get; set; }

        public Rectangle WorkingArea { get; set; }

        public string DeviceName { get; set; }
        
        public bool Primary { get; set; }

        public bool IsVirtualScreen => DeviceName == nameof(SystemInformation.VirtualScreen);

        public static ScreenInfo FromScreen(Screen screen)
        {            
            return new ScreenInfo
            {
                Bounds = screen.Bounds,
                WorkingArea = screen.WorkingArea,                
                DeviceName = screen.DeviceName,
                Primary = screen.Primary,                
            };
        }

        public static ScreenInfo FromPrimaryScreen()
        {
            return FromScreen(Screen.PrimaryScreen);
        }

        public static ScreenInfo FromVirtualScreen()
        {
            return new ScreenInfo
            {
                DeviceName = nameof(SystemInformation.VirtualScreen),
                Bounds = SystemInformation.VirtualScreen
            };
        }

        public static List<ScreenInfo> FromAllScreens()
        {
            List<ScreenInfo> screens = new List<ScreenInfo>();
            
            foreach (var screen in Screen.AllScreens)
            {
                screens.Add(FromScreen(screen));
            }

            return screens;
        }
    }
}
