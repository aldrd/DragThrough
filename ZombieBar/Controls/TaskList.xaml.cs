using ManagedShell.AppBar;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ZombieBar.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace ZombieBar.Controls
{
    /// <summary>
    /// Interaction logic for TaskList.xaml
    /// </summary>
    public partial class TaskList : UserControl
    {
        private bool isLoaded;
        private double DefaultButtonWidth;
        private double MinButtonWidth;
        private double TaskButtonLeftMargin;
        private double TaskButtonRightMargin;
        private ListCollectionView _windowsView;
        private TabStripDragHandler _dragHandler;
        private VirtualDesktopService _virtualDesktopService;

        // After a desktop switch the DWM cloak/uncloak of each window can lag the registry flag we
        // poll, so we re-run the filter once more a moment later to catch late-settling windows.
        private DispatcherTimer _desktopSettleTimer;

        public static DependencyProperty ButtonWidthProperty = DependencyProperty.Register("ButtonWidth", typeof(double), typeof(TaskList), new PropertyMetadata(new double()));

        public double ButtonWidth
        {
            get { return (double)GetValue(ButtonWidthProperty); }
            set { SetValue(ButtonWidthProperty, value); }
        }

        public static DependencyProperty TasksProperty = DependencyProperty.Register("Tasks", typeof(Tasks), typeof(TaskList));

        public Tasks Tasks
        {
            get { return (Tasks)GetValue(TasksProperty); }
            set { SetValue(TasksProperty, value); }
        }

        public TaskList()
        {
            InitializeComponent();
        }

        private void SetStyles()
        {
            DefaultButtonWidth = Application.Current.FindResource("TaskButtonWidth") as double? ?? 0;
            MinButtonWidth = Application.Current.FindResource("TaskButtonMinWidth") as double? ?? 0;
            Thickness buttonMargin;

            if (Settings.Instance.Edge == (int)AppBarEdge.Left || Settings.Instance.Edge == (int)AppBarEdge.Right)
            {
                buttonMargin = Application.Current.FindResource("TaskButtonVerticalMargin") as Thickness? ?? new Thickness();
            }
            else
            {
                buttonMargin = Application.Current.FindResource("TaskButtonMargin") as Thickness? ?? new Thickness();
            }

            TaskButtonLeftMargin = buttonMargin.Left;
            TaskButtonRightMargin = buttonMargin.Right;
        }

        private void TaskList_OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!isLoaded && Tasks != null)
            {
                if (Tasks.Windows != null)
                {
                    App app = Application.Current as App;
                    _virtualDesktopService = app?.VirtualDesktopService;

                    // Independent view (not the shared default view): show only taskbar windows that
                    // belong to the current virtual desktop, ordered by their order identifier, with
                    // live re-sort/re-filter on changes.
                    _windowsView = new ListCollectionView(Tasks.Windows)
                    {
                        Filter = w => w is ApplicationWindow window
                                      && window.ShowInTaskbar
                                      && IsOnCurrentDesktop(window.Handle),
                        CustomSort = TasksOrderManager.Comparer,
                        IsLiveFiltering = true,
                        IsLiveSorting = true
                    };
                    _windowsView.LiveFilteringProperties.Add("ShowInTaskbar");
                    _windowsView.LiveSortingProperties.Add("Order");

                    TasksList.ItemsSource = _windowsView;
                    Tasks.Windows.CollectionChanged += GroupedWindows_CollectionChanged;

                    // Re-filter when the user switches virtual desktops.
                    if (_virtualDesktopService != null)
                        _virtualDesktopService.DesktopChanged += VirtualDesktopService_DesktopChanged;

                    _desktopSettleTimer = new DispatcherTimer(DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(400)
                    };
                    _desktopSettleTimer.Tick += DesktopSettleTimer_Tick;

                    TasksOrderManager orderManager = app?.TasksOrderManager;
                    if (orderManager != null && _dragHandler == null)
                        _dragHandler = new TabStripDragHandler(TasksList, _windowsView, orderManager);
                }

                isLoaded = true;
            }

            SetStyles();
        }

        private void TaskList_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (isLoaded && Tasks?.Windows != null)
            {
                Tasks.Windows.CollectionChanged -= GroupedWindows_CollectionChanged;
            }

            if (_desktopSettleTimer != null)
            {
                _desktopSettleTimer.Stop();
                _desktopSettleTimer.Tick -= DesktopSettleTimer_Tick;
                _desktopSettleTimer = null;
            }

            if (_virtualDesktopService != null)
            {
                _virtualDesktopService.DesktopChanged -= VirtualDesktopService_DesktopChanged;
                _virtualDesktopService = null;
            }

            TasksList.ItemsSource = null;
            _windowsView = null;
            isLoaded = false;
        }

        // A window on another virtual desktop is DWM-cloaked (DWMWA_CLOAKED != 0); one on the current
        // desktop is not — including minimized windows. This is the same signal ManagedShell uses for
        // ShowInTaskbar, but read live here: a window that uncloaks/settles after a desktop switch is
        // then re-evaluated immediately. (ManagedShell only re-checks on uncloak, and the previous
        // COM-based approach cached a stale membership set that missed such windows — the cause of
        // "sometimes not all windows show; restoring one makes it appear".) DwmGetWindowAttribute is a
        // plain P/Invoke with no message pump, so unlike the COM API it is safe to call from the filter.
        private static bool IsOnCurrentDesktop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return true;

            try
            {
                int hr = NativeMethods.DwmGetWindowAttribute(
                    hwnd, NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, out uint cloaked, sizeof(uint));
                if (hr != 0)
                    return true; // couldn't determine -> don't hide it (fail open)
                return cloaked == 0;
            }
            catch
            {
                return true;
            }
        }

        // Current virtual desktop changed: re-run the filter so windows that were cloaked/uncloaked by
        // the switch are re-evaluated.
        private void VirtualDesktopService_DesktopChanged(object sender, EventArgs e)
        {
            _windowsView?.Refresh();

            // The cloak/uncloak that accompanies the switch can lag the registry flag we poll, so
            // re-run once more shortly after to catch windows whose cloak state settled late.
            _desktopSettleTimer?.Stop();
            _desktopSettleTimer?.Start();
        }

        private void DesktopSettleTimer_Tick(object sender, EventArgs e)
        {
            _desktopSettleTimer?.Stop();
            if (isLoaded)
                _windowsView?.Refresh();
        }

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            SetTaskButtonWidth();
        }

        private void TaskList_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            SetTaskButtonWidth();
        }

        private void SetTaskButtonWidth()
        {
            if (Settings.Instance.Edge == (int)AppBarEdge.Left || Settings.Instance.Edge == (int)AppBarEdge.Right)
            {
                ButtonWidth = ActualWidth;
                return;
            }

            if (TasksList.Items.Count == 0)
            {
                return;
            }

            double margin = TaskButtonLeftMargin + TaskButtonRightMargin;
            double maxWidth = TasksList.ActualWidth / TasksList.Items.Count;
            double defaultWidth = DefaultButtonWidth + margin;
            double minWidth = MinButtonWidth + margin;

            if (maxWidth > defaultWidth)
            {
                ButtonWidth = DefaultButtonWidth;
            }
            else if (maxWidth < minWidth)
            {
                // More windows than fit even at the minimum width: keep buttons at the minimum so the
                // maximum number stay in the single visible row; the genuine overflow wraps to a
                // second row that the (scroll-disabled) ScrollViewer clips away.
                ButtonWidth = MinButtonWidth;
            }
            else
            {
                ButtonWidth = Math.Floor(maxWidth);
            }
        }
    }
}
